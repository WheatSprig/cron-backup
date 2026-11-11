using CronBackup.Core.Storage;

namespace CronBackup.Core.Retention;

public sealed record RetentionApplyResult(
    IReadOnlyList<string> Deleted,
    int Pending,
    bool Truncated);

/// <summary>run 和 prune 共用的清理执行器，避免两份删除逻辑漂移。</summary>
public static class RetentionExecutor
{
    /// <summary>读取三档对象并生成保留计划；prune 与 run 都走这里。</summary>
    public static async Task<RetentionPlan> BuildPlanAsync(
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

    public static async Task<RetentionApplyResult> ApplyAsync(
        IStorageBackend backend,
        RetentionPlan plan,
        CancellationToken cancellationToken)
    {
        var keys = plan.Prune
            .SelectMany(item => item.ToDelete)
            .Select(item => item.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (keys.Count > 0)
        {
            await backend.DeleteManyAsync(keys, cancellationToken);
        }

        var pending = plan.PendingDeletes;
        return new RetentionApplyResult(keys, pending, pending > 0);
    }
}
