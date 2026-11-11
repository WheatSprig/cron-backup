using CronBackup.Core.Storage;

namespace CronBackup.Core.Retention;

public sealed class RetentionAbortedException : Exception
{
    public RetentionAbortedException(string message)
        : base(message)
    {
    }
}

public static class RetentionService
{
    public static async Task<RetentionPlan> PlanAsync(
        IStorageBackend backend,
        ResolvedRetention policy,
        DateTimeOffset now,
        TimeZoneInfo timezone,
        string? currentKey,
        CancellationToken cancellationToken)
    {
        var daily = await backend.ListAsync("daily", cancellationToken);
        var weekly = await backend.ListAsync("weekly", cancellationToken);
        var monthly = await backend.ListAsync("monthly", cancellationToken);

        return RetentionPlanner.BuildPlan(
            policy,
            daily,
            weekly,
            monthly,
            now,
            timezone,
            currentKey);
    }

    // 调用方应先把 plan 展示给用户；这里只执行已经算好的 ToDelete。
    // 任一 tier 超限都拒绝整批删除，避免“删一半”造成不可控状态。
    public static async Task<IReadOnlyList<string>> ApplyAsync(
        IStorageBackend backend,
        RetentionPlan plan,
        CancellationToken cancellationToken)
    {
        var aborted = plan.Prune.FirstOrDefault(item => item.Aborted);
        if (aborted is not null)
        {
            throw new RetentionAbortedException(
                $"retention candidates for {aborted.Tier} exceed max_delete {aborted.MaxDelete}");
        }

        var keys = plan.Prune
            .SelectMany(item => item.ToDelete)
            .Select(item => item.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (keys.Count > 0)
        {
            await backend.DeleteManyAsync(keys, cancellationToken);
        }

        return keys;
    }
}
