using CronBackup.Core.Storage;

namespace CronBackup.Core.Retention;

public enum StorageTier
{
    Daily,
    Weekly,
    Monthly,
}

public sealed record PromotionDecision(bool Weekly, bool Monthly);

public sealed record PrunePlan(
    StorageTier Tier,
    IReadOnlyList<RemoteObject> Candidates,
    IReadOnlyList<RemoteObject> ToDelete,
    int MaxDelete,
    bool Aborted);

public sealed record RetentionPlan(
    ResolvedRetention Policy,
    PromotionDecision Promotion,
    IReadOnlyList<PrunePlan> Prune)
{
    public bool HasWork =>
        Promotion.Weekly ||
        Promotion.Monthly ||
        Prune.Any(plan => plan.ToDelete.Count > 0);
}

public static class RetentionPlanner
{
    private static readonly TimeSpan WeeklyCatchUpWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan MonthlyCatchUpWindow = TimeSpan.FromDays(31);

    public static RetentionPlan BuildPlan(
        ResolvedRetention policy,
        IReadOnlyList<RemoteObject> daily,
        IReadOnlyList<RemoteObject> weekly,
        IReadOnlyList<RemoteObject> monthly,
        DateTimeOffset now,
        TimeZoneInfo timezone,
        string? currentKey = null)
    {
        if (!policy.Enabled)
        {
            return new RetentionPlan(
                policy,
                new PromotionDecision(false, false),
                Array.Empty<PrunePlan>());
        }

        var localNow = TimeZoneInfo.ConvertTime(now, timezone);
        var weeklyRecent = weekly.Any(item => item.Modified > now - WeeklyCatchUpWindow);
        var monthlyRecent = monthly.Any(item => item.Modified > now - MonthlyCatchUpWindow);

        var promotion = new PromotionDecision(
            Weekly: policy.Weekly > 0 &&
                     (localNow.DayOfWeek == DayOfWeek.Sunday || !weeklyRecent),
            Monthly: policy.Monthly > 0 &&
                     (localNow.Day == 1 || !monthlyRecent));

        var prunePlans = new[]
        {
            BuildPrunePlan(StorageTier.Daily, daily, policy.Daily, policy.MaxDelete, currentKey),
            BuildPrunePlan(StorageTier.Weekly, weekly, policy.Weekly, policy.MaxDelete, null),
            BuildPrunePlan(StorageTier.Monthly, monthly, policy.Monthly, policy.MaxDelete, null),
        };

        return new RetentionPlan(policy, promotion, prunePlans);
    }

    private static PrunePlan BuildPrunePlan(
        StorageTier tier,
        IReadOnlyList<RemoteObject> objects,
        int keep,
        int maxDelete,
        string? protectedKey)
    {
        if (keep <= 0)
        {
            return new PrunePlan(tier, Array.Empty<RemoteObject>(), Array.Empty<RemoteObject>(), maxDelete, false);
        }

        var ordered = objects
            .OrderByDescending(item => item.Modified)
            .ThenByDescending(item => item.Key, StringComparer.Ordinal)
            .ToList();

        var retainedKeys = ordered
            .Take(keep)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (protectedKey is not null && !retainedKeys.Contains(protectedKey))
        {
            var current = ordered.FirstOrDefault(item =>
                string.Equals(item.Key, protectedKey, StringComparison.Ordinal));
            if (current is not null)
            {
                if (retainedKeys.Count == keep)
                {
                    retainedKeys.Remove(ordered[keep - 1].Key);
                }

                retainedKeys.Add(current.Key);
            }
        }

        var candidates = ordered
            .Where(item => !retainedKeys.Contains(item.Key))
            .ToList();

        var aborted = candidates.Count > maxDelete;
        return new PrunePlan(
            tier,
            candidates,
            aborted ? Array.Empty<RemoteObject>() : candidates,
            maxDelete,
            aborted);
    }
}
