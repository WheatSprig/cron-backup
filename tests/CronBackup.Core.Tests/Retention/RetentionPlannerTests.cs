using CronBackup.Core.Configuration;
using CronBackup.Core.Retention;
using CronBackup.Core.Storage;

namespace CronBackup.Core.Tests.Retention;

public sealed class RetentionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Resolve_ShouldMergeDefaultsJobAndDestination()
    {
        var defaults = new RetentionPolicy
        {
            Daily = 7,
            Weekly = 4,
            Monthly = 6,
            MaxDelete = 20,
        };
        var job = new RetentionPolicy { Daily = 14 };
        var destination = new RetentionPolicy { Weekly = 8, MaxDelete = 50 };

        var result = RetentionResolver.Resolve(defaults, job, destination);

        Assert.True(result.Enabled);
        Assert.Equal(14, result.Daily);
        Assert.Equal(8, result.Weekly);
        Assert.Equal(6, result.Monthly);
        Assert.Equal(50, result.MaxDelete);
    }

    [Fact]
    public void Resolve_ShouldDisableWhenJobIsDisabled()
    {
        var defaults = new RetentionPolicy { Daily = 7 };
        var job = new RetentionPolicy { Enabled = false };
        var destination = new RetentionPolicy { Daily = 30 };

        var result = RetentionResolver.Resolve(defaults, job, destination);

        Assert.False(result.Enabled);
    }

    [Fact]
    public void Resolve_ShouldDisableWhenDestinationIsDisabled()
    {
        var result = RetentionResolver.Resolve(
            new RetentionPolicy { Daily = 7 },
            null,
            new RetentionPolicy { Enabled = false });

        Assert.False(result.Enabled);
    }

    [Fact]
    public void Resolve_ShouldDisableWhenAllTiersAreZero()
    {
        var result = RetentionResolver.Resolve(
            new RetentionPolicy { Daily = 0, Weekly = 0, Monthly = 0 },
            null,
            null);

        Assert.False(result.Enabled);
    }

    [Fact]
    public void BuildPlan_ShouldKeepNewestAndDeleteOlderObjects()
    {
        var daily = Objects(
            ("daily/oldest", Now.AddDays(-3)),
            ("daily/older", Now.AddDays(-2)),
            ("daily/newer", Now.AddDays(-1)),
            ("daily/newest", Now));
        var policy = new ResolvedRetention(true, 2, 0, 0, 20);

        var plan = RetentionPlanner.BuildPlan(
            policy,
            daily,
            Array.Empty<RemoteObject>(),
            Array.Empty<RemoteObject>(),
            Now,
            TimeZoneInfo.Utc);

        var prune = Assert.Single(plan.Prune, item => item.Tier == StorageTier.Daily);
        Assert.Equal(2, prune.ToDelete.Count);
        Assert.Contains(prune.ToDelete, item => item.Key == "daily/oldest");
        Assert.Contains(prune.ToDelete, item => item.Key == "daily/older");
        Assert.False(prune.Truncated);
    }

    [Fact]
    public void BuildPlan_ShouldKeepOneObjectPerLocalDay()
    {
        var tz = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 11, 11, 13, 0, 0, TimeSpan.Zero);
        var policy = new ResolvedRetention(true, Daily: 2, Weekly: 0, Monthly: 0, MaxDelete: 20);

        var daily = new[]
        {
            new RemoteObject("daily/backup_20261109_063000_000.tar.gz", 10, now.AddDays(-2)),
            new RemoteObject("daily/backup_20261110_063000_000.tar.gz", 10, now.AddDays(-1)),
            new RemoteObject("daily/backup_20261111_063000_000.tar.gz", 10, now),
            new RemoteObject("daily/backup_20261111_120000_000.tar.gz", 10, now), // 同一天第二次
        };

        var plan = RetentionPlanner.BuildPlan(
            policy,
            daily,
            Array.Empty<RemoteObject>(),
            Array.Empty<RemoteObject>(),
            now,
            tz,
            "daily/backup_20261111_120000_000.tar.gz");

        var prune = Assert.Single(plan.Prune, item => item.Tier == StorageTier.Daily);

        // 保留 11-11（12:00）和 11-10；删掉同日较早的一份和 11-09
        Assert.Equal(
            new[] { "daily/backup_20261109_063000_000.tar.gz", "daily/backup_20261111_063000_000.tar.gz" },
            prune.ToDelete.Select(item => item.Key).OrderBy(key => key));
    }

    [Fact]
    public void BuildPlan_ShouldProtectCurrentKeyEvenWhenItIsOldest()
    {
        var daily = Objects(
            ("daily/current", Now.AddDays(-3)),
            ("daily/newer", Now),
            ("daily/newest", Now.AddDays(1)));
        var policy = new ResolvedRetention(true, 2, 0, 0, 20);

        var plan = RetentionPlanner.BuildPlan(
            policy,
            daily,
            Array.Empty<RemoteObject>(),
            Array.Empty<RemoteObject>(),
            Now,
            TimeZoneInfo.Utc,
            "daily/current");

        var prune = Assert.Single(plan.Prune, item => item.Tier == StorageTier.Daily);
        Assert.Equal(new[] { "daily/newer" }, prune.ToDelete.Select(item => item.Key));
    }

    [Fact]
    public void BuildPlan_ShouldTruncateWhenCandidatesExceedMaxDelete()
    {
        var daily = Objects(
            ("daily/one", Now.AddDays(-5)),
            ("daily/two", Now.AddDays(-4)),
            ("daily/three", Now.AddDays(-3)),
            ("daily/four", Now.AddDays(-2)),
            ("daily/five", Now.AddDays(-1)));
        var policy = new ResolvedRetention(true, 1, 0, 0, 2);

        var plan = RetentionPlanner.BuildPlan(
            policy,
            daily,
            Array.Empty<RemoteObject>(),
            Array.Empty<RemoteObject>(),
            Now,
            TimeZoneInfo.Utc);

        var prune = Assert.Single(plan.Prune, item => item.Tier == StorageTier.Daily);
        Assert.True(prune.Truncated);
        Assert.Equal(4, prune.Candidates.Count);
        Assert.Equal(2, prune.ToDelete.Count);
        Assert.Equal(2, prune.Deferred.Count);
        Assert.Equal(2, plan.PendingDeletes);
    }

    [Fact]
    public void BuildPlan_ShouldUseKeyTimestampForWeeklyCatchUp()
    {
        var tz = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 11, 11, 13, 0, 0, TimeSpan.Zero);
        var policy = new ResolvedRetention(true, Daily: 0, Weekly: 5, Monthly: 0, MaxDelete: 20);

        // mtime 很新，但 key 里的日期是 40 天前 —— 不应算作「最近有周备」
        var weekly = new[]
        {
            new RemoteObject("weekly/backup_20261002_063000_000.tar.gz", 10, now.AddHours(-1)),
        };

        var plan = RetentionPlanner.BuildPlan(
            policy, Array.Empty<RemoteObject>(), weekly, Array.Empty<RemoteObject>(), now, tz);

        Assert.True(plan.Promotion.Weekly);
    }

    [Fact]
    public void ArchiveKey_ShouldParseLegacyAndV2Names()
    {
        Assert.True(ArchiveKey.TryGetTimestamp("daily/backup_20261111_063000_123.tar.gz", out var v2));
        Assert.Equal(new DateTime(2026, 11, 11, 6, 30, 0, 123), v2);

        Assert.True(ArchiveKey.TryGetTimestamp("daily/backup_20261111_063000.tar.gz", out var legacy));
        Assert.Equal(new DateTime(2026, 11, 11, 6, 30, 0), legacy);

        Assert.False(ArchiveKey.TryGetTimestamp("daily/manual-upload.sql.gz", out _));
    }

    [Fact]
    public void BuildPlan_ShouldPromoteOnSunday()
    {
        var monthly = Objects(("monthly/recent", Now.AddDays(-1)));

        var plan = RetentionPlanner.BuildPlan(
            new ResolvedRetention(true, 7, 4, 6, 20),
            Array.Empty<RemoteObject>(),
            Array.Empty<RemoteObject>(),
            monthly,
            Now,
            TimeZoneInfo.Utc);

        Assert.True(plan.Promotion.Weekly);
        Assert.False(plan.Promotion.Monthly);
    }

    [Fact]
    public void BuildPlan_ShouldNotPromoteWeeklyAgainWithinSevenDays()
    {
        var weekly = Objects(("weekly/last", Now.AddDays(-1)));

        var plan = RetentionPlanner.BuildPlan(
            new ResolvedRetention(true, 7, 4, 6, 20),
            Array.Empty<RemoteObject>(),
            weekly,
            Array.Empty<RemoteObject>(),
            Now.AddDays(1),
            TimeZoneInfo.Utc);

        Assert.False(plan.Promotion.Weekly);
    }

    [Fact]
    public void BuildPlan_ShouldCatchUpWeeklyWhenSundayWasMissed()
    {
        var weekly = Objects(("weekly/old", Now.AddDays(-8)));

        var plan = RetentionPlanner.BuildPlan(
            new ResolvedRetention(true, 7, 4, 6, 20),
            Array.Empty<RemoteObject>(),
            weekly,
            Array.Empty<RemoteObject>(),
            Now.AddDays(1),
            TimeZoneInfo.Utc);

        Assert.True(plan.Promotion.Weekly);
    }

    [Fact]
    public void BuildPlan_ShouldPromoteMonthlyOnFirstDay()
    {
        var firstOfMonth = new DateTimeOffset(2026, 12, 1, 2, 0, 0, TimeSpan.Zero);

        var plan = RetentionPlanner.BuildPlan(
            new ResolvedRetention(true, 7, 4, 6, 20),
            Array.Empty<RemoteObject>(),
            Array.Empty<RemoteObject>(),
            Array.Empty<RemoteObject>(),
            firstOfMonth,
            TimeZoneInfo.Utc);

        Assert.True(plan.Promotion.Monthly);
    }

    [Fact]
    public void BuildPlan_ShouldCatchUpMonthlyWhenFirstDayWasMissed()
    {
        var monthly = Objects(("monthly/old", Now.AddDays(-40)));

        var plan = RetentionPlanner.BuildPlan(
            new ResolvedRetention(true, 7, 4, 6, 20),
            Array.Empty<RemoteObject>(),
            Array.Empty<RemoteObject>(),
            monthly,
            Now.AddDays(2),
            TimeZoneInfo.Utc);

        Assert.True(plan.Promotion.Monthly);
    }

    [Fact]
    public void BuildPlan_ShouldUseJobTimezoneForSundayBoundary()
    {
        var shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var mondayInShanghai = new DateTimeOffset(2026, 11, 8, 16, 0, 0, TimeSpan.Zero);
        var weekly = Objects(("weekly/recent", mondayInShanghai.AddHours(-12)));

        var plan = RetentionPlanner.BuildPlan(
            new ResolvedRetention(true, 7, 4, 6, 20),
            Array.Empty<RemoteObject>(),
            weekly,
            Array.Empty<RemoteObject>(),
            mondayInShanghai,
            shanghai);

        Assert.False(plan.Promotion.Weekly);
    }

    [Fact]
    public void BuildPlan_ShouldReturnNoWorkWhenDisabled()
    {
        var plan = RetentionPlanner.BuildPlan(
            ResolvedRetention.Disabled,
            Objects(("daily/one", Now)),
            Array.Empty<RemoteObject>(),
            Array.Empty<RemoteObject>(),
            Now,
            TimeZoneInfo.Utc);

        Assert.False(plan.HasWork);
    }

    private static List<RemoteObject> Objects(params (string Key, DateTimeOffset Modified)[] items) =>
        items.Select(item => new RemoteObject(item.Key, 1, item.Modified)).ToList();
}
