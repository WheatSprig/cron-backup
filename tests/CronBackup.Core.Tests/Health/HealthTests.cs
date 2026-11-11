using CronBackup.Core.Health;
using CronBackup.Core.Tests.TestHelpers;

namespace CronBackup.Core.Tests.Health;

public sealed class HealthTests
{
    [Fact]
    public async Task Store_ShouldRoundTripStateAndUpdate()
    {
        using var temp = new TempDirectory();
        var store = new FileHealthStore(Path.Combine(temp.Path, "status.json"));
        var now = DateTimeOffset.UtcNow;

        await store.WriteAsync(new HealthState
        {
            ConfigValid = true,
            SchedulerRunning = true,
            StartedAt = now,
            HeartbeatAt = now,
        }, CancellationToken.None);
        await store.UpdateAsync(state => state.ConsecutiveFailures = 2, CancellationToken.None);

        var state = await store.ReadAsync(CancellationToken.None);
        Assert.NotNull(state);
        Assert.True(state!.SchedulerRunning);
        Assert.Equal(2, state.ConsecutiveFailures);
    }

    [Fact]
    public void Evaluate_ShouldBeHealthyWithFreshHeartbeat()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new HealthState
        {
            ConfigValid = true,
            SchedulerRunning = true,
            HeartbeatAt = now.AddSeconds(-10),
        };

        var result = HealthEvaluator.Evaluate(state, now, TimeSpan.FromSeconds(90), false);

        Assert.True(result.Healthy);
    }

    [Fact]
    public void Evaluate_ShouldRejectStaleHeartbeat()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new HealthState
        {
            ConfigValid = true,
            SchedulerRunning = true,
            HeartbeatAt = now.AddMinutes(-5),
        };

        var result = HealthEvaluator.Evaluate(state, now, TimeSpan.FromSeconds(90), false);

        Assert.False(result.Healthy);
        Assert.Contains(result.Errors, error => error.Contains("stale", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_ShouldRejectFailedLastRunInStrictMode()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new HealthState
        {
            ConfigValid = true,
            SchedulerRunning = true,
            HeartbeatAt = now,
            LastRun = new JobHealthSnapshot { Job = "photos", Success = false, Error = "boom" },
            ConsecutiveFailures = 3,
        };

        var result = HealthEvaluator.Evaluate(state, now, TimeSpan.FromSeconds(90), true);

        Assert.False(result.Healthy);
        Assert.Contains(result.Errors, error => error.Contains("last run failed", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("consecutive failures", StringComparison.Ordinal));
    }
}
