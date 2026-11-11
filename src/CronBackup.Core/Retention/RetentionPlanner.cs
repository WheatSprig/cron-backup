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

        // 提升规则：周日/每月 1 号执行；如果那天漏跑，下一次运行用“最近 7/31 天
        // 没有 weekly/monthly”作为补跑条件。这里用滚动窗口而不是日历周/月，
        // 避免周一补跑时把周日刚提升的对象又复制一次。
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

        // 保留最新 N 个；当前刚上传的 key 必须留在保留集合里。如果它恰好排在
        // 第 N 名之外，就挤掉当前集合里最旧的一个，保证总数仍是 N。
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

        // 超过硬上限时整批中止，绝不“删一部分”。把 Candidates 留给 plan 输出，
        // ToDelete 置空，调用方据此决定报警而不是部分执行。
        var aborted = candidates.Count > maxDelete;
        return new PrunePlan(
            tier,
            candidates,
            aborted ? Array.Empty<RemoteObject>() : candidates,
            maxDelete,
            aborted);
    }
}
