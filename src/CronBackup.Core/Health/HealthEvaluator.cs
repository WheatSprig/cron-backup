namespace CronBackup.Core.Health;

public static class HealthEvaluator
{
    public static HealthCheckResult Evaluate(
        HealthState? state,
        DateTimeOffset now,
        TimeSpan maxHeartbeatAge,
        bool strict)
    {
        var errors = new List<string>();
        if (state is null)
        {
            errors.Add("status file not found");
            return new HealthCheckResult(false, errors);
        }

        if (!state.ConfigValid)
        {
            errors.Add("configuration is not valid");
        }

        if (!state.SchedulerRunning)
        {
            errors.Add("scheduler is not running");
        }

        if (now - state.HeartbeatAt > maxHeartbeatAge)
        {
            errors.Add($"scheduler heartbeat is stale ({now - state.HeartbeatAt})");
        }

        if (strict)
        {
            if (state.LastRun is { Success: false })
            {
                errors.Add($"last run failed: {state.LastRun.Error ?? "unknown error"}");
            }

            if (state.ConsecutiveFailures >= 3)
            {
                errors.Add($"consecutive failures: {state.ConsecutiveFailures}");
            }
        }

        return new HealthCheckResult(errors.Count == 0, errors);
    }
}
