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
    IReadOnlyList<RemoteObject> Deferred,
    int MaxDelete)
{
    /// <summary>候选超过 max_delete，本次只删前 max_delete 个，剩下的下次继续。</summary>
    public bool Truncated => Deferred.Count > 0;
}

public sealed record RetentionPlan(
    ResolvedRetention Policy,
    PromotionDecision Promotion,
    IReadOnlyList<PrunePlan> Prune)
{
    public bool HasWork =>
        Promotion.Weekly ||
        Promotion.Monthly ||
        Prune.Any(plan => plan.ToDelete.Count > 0);

    public int PendingDeletes => Prune.Sum(plan => plan.Deferred.Count);
}

public static class RetentionPlanner
{
    private const int WeeklyCatchUpDays = 7;
    private const int MonthlyCatchUpDays = 31;

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

        var promotion = BuildPromotion(policy, weekly, monthly, now, timezone);

        var prune = new[]
        {
            BuildPrunePlan(StorageTier.Daily, daily, policy.Daily, policy.MaxDelete, currentKey, timezone),
            BuildPrunePlan(StorageTier.Weekly, weekly, policy.Weekly, policy.MaxDelete, null, timezone),
            BuildPrunePlan(StorageTier.Monthly, monthly, policy.Monthly, policy.MaxDelete, null, timezone),
        };

        return new RetentionPlan(policy, promotion, prune);
    }

    /// <summary>
    /// 提升判定。补跑窗口用「最近 7/31 天有没有 weekly/monthly」而不是日历周/月，
    /// 避免周一补跑时把周日刚提升的对象再复制一次。
    /// 这里的时间以 key 内嵌时间戳为准，不依赖后端 mtime。
    /// </summary>
    public static PromotionDecision BuildPromotion(
        ResolvedRetention policy,
        IReadOnlyList<RemoteObject> weekly,
        IReadOnlyList<RemoteObject> monthly,
        DateTimeOffset now,
        TimeZoneInfo timezone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, timezone);
        var today = DateOnly.FromDateTime(localNow.DateTime);

        return new PromotionDecision(
            Weekly: policy.Weekly > 0 &&
                    (localNow.DayOfWeek == DayOfWeek.Sunday ||
                     !HasRecentObject(weekly, today, WeeklyCatchUpDays, timezone)),
            Monthly: policy.Monthly > 0 &&
                     (localNow.Day == 1 ||
                      !HasRecentObject(monthly, today, MonthlyCatchUpDays, timezone)));
    }

    private static bool HasRecentObject(
        IReadOnlyList<RemoteObject> objects,
        DateOnly today,
        int days,
        TimeZoneInfo timezone) =>
        objects.Any(item => GetLocalDate(item, timezone) > today.AddDays(-days));

    private static PrunePlan BuildPrunePlan(
        StorageTier tier,
        IReadOnlyList<RemoteObject> objects,
        int keep,
        int maxDelete,
        string? protectedKey,
        TimeZoneInfo timezone)
    {
        if (keep <= 0 || objects.Count == 0)
        {
            return new PrunePlan(
                tier,
                Array.Empty<RemoteObject>(),
                Array.Empty<RemoteObject>(),
                Array.Empty<RemoteObject>(),
                maxDelete);
        }

        // 1) 按本地日期分组，每天一个“代表”（当天最新的一份）。
        //    同一天的其他副本一律算候选，这是修掉「N 次运行 = N 天」的关键。
        var representatives = objects
            .GroupBy(item => GetLocalDate(item, timezone))
            .Select(group => group
                .OrderByDescending(item => ArchiveKey.SortKey(item.Key), StringComparer.Ordinal)
                .ThenByDescending(item => item.Modified)
                .First())
            .OrderByDescending(item => GetLocalDate(item, timezone))
            .ToList();

        // 2) 保护刚上传的 key：如果它是某天的非代表副本，把它升为该天的代表。
        if (!string.IsNullOrEmpty(protectedKey) &&
            representatives.All(item => !string.Equals(item.Key, protectedKey, StringComparison.Ordinal)))
        {
            var current = objects.FirstOrDefault(item =>
                string.Equals(item.Key, protectedKey, StringComparison.Ordinal));

            if (current is not null)
            {
                var date = GetLocalDate(current, timezone);
                var index = representatives.FindIndex(item => GetLocalDate(item, timezone) == date);

                if (index >= 0)
                {
                    representatives[index] = current;
                }
                else
                {
                    representatives.Add(current);
                }
            }
        }

        // 3) 只保留最近 keep 天。
        var keptKeys = new HashSet<string>(
            representatives.Take(keep).Select(item => item.Key),
            StringComparer.Ordinal);

        // 4) 被保护的 key 若落在窗口外，挤掉最旧的一天（保证总数不变）。
        if (!string.IsNullOrEmpty(protectedKey) && !keptKeys.Contains(protectedKey))
        {
            var protectedItem = representatives.FirstOrDefault(item =>
                string.Equals(item.Key, protectedKey, StringComparison.Ordinal));

            if (protectedItem is not null)
            {
                if (keptKeys.Count >= keep)
                {
                    var oldest = representatives.Take(keep).LastOrDefault();
                    if (oldest is not null)
                    {
                        keptKeys.Remove(oldest.Key);
                    }
                }

                keptKeys.Add(protectedItem.Key);
            }
        }

        // 5) 其余全部进候选；按 key 排序保证输出稳定。
        var candidates = objects
            .Where(item => !keptKeys.Contains(item.Key))
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToList();

        // 6) 单次删除上限：截断而不是整批放弃，剩余留给下次运行。
        var limit = Math.Max(0, maxDelete);
        var toDelete = candidates.Take(limit).ToList();
        var deferred = candidates.Skip(toDelete.Count).ToList();

        return new PrunePlan(tier, candidates, toDelete, deferred, maxDelete);
    }

    private static DateOnly GetLocalDate(RemoteObject item, TimeZoneInfo timezone)
    {
        if (ArchiveKey.TryGetTimestamp(item.Key, out var local))
        {
            return DateOnly.FromDateTime(local);
        }

        // 兼容测试与历史命名：key 里没有时间戳时退回远端 mtime。
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.Modified, timezone).DateTime);
    }
}
