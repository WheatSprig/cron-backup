namespace CronBackup.Core.Health;

public sealed class HealthState
{
    public string Version { get; set; } = "2.0.0-alpha.1";

    public string Mode { get; set; } = "daemon";

    public bool ConfigValid { get; set; }

    public bool SchedulerRunning { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset HeartbeatAt { get; set; }

    public JobHealthSnapshot? LastRun { get; set; }

    public int ConsecutiveFailures { get; set; }
}

public sealed class JobHealthSnapshot
{
    public string Job { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }
}

public sealed record HealthCheckResult(bool Healthy, IReadOnlyList<string> Errors);
