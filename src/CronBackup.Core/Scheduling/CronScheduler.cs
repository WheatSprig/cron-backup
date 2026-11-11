using Cronos;
using CronBackup.Core.Configuration;
using CronBackup.Core.Health;
using Microsoft.Extensions.Logging;

namespace CronBackup.Core.Scheduling;

public sealed class CronScheduler
{
    private readonly ILogger<CronScheduler> _logger;
    private readonly IHealthStore? _healthStore;
    private readonly TimeProvider _timeProvider;

    public CronScheduler(
        ILogger<CronScheduler> logger,
        IHealthStore? healthStore = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _healthStore = healthStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RunAsync(
        RootConfig config,
        Func<JobConfig, CancellationToken, Task> runJob,
        CancellationToken cancellationToken)
    {
        var jobs = config.Jobs.Where(job => job.IsEnabled).ToArray();
        if (jobs.Length == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (_healthStore is not null)
        {
            await _healthStore.WriteAsync(
                new HealthState
                {
                    Mode = "daemon",
                    ConfigValid = true,
                    SchedulerRunning = true,
                    StartedAt = now,
                    HeartbeatAt = now,
                },
                cancellationToken);
        }

        using var heartbeatSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = HeartbeatAsync(heartbeatSource.Token);
        var tasks = jobs
            .Select(job => RunJobLoopAsync(job, config.Defaults.Timezone, runJob, cancellationToken))
            .ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await heartbeatSource.CancelAsync();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
            }

            if (_healthStore is not null)
            {
                await _healthStore.UpdateAsync(
                    state =>
                    {
                        state.SchedulerRunning = false;
                        state.HeartbeatAt = _timeProvider.GetUtcNow();
                    },
                    CancellationToken.None);
            }
        }
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        if (_healthStore is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _timeProvider, cancellationToken);
            await _healthStore.UpdateAsync(
                state =>
                {
                    state.SchedulerRunning = true;
                    state.HeartbeatAt = _timeProvider.GetUtcNow();
                },
                cancellationToken);
        }
    }

    private async Task RunJobLoopAsync(
        JobConfig job,
        string? defaultTimezone,
        Func<JobConfig, CancellationToken, Task> runJob,
        CancellationToken cancellationToken)
    {
        if (job.IntervalSeconds is { } intervalSeconds)
        {
            var interval = TimeSpan.FromSeconds(intervalSeconds);
            while (!cancellationToken.IsCancellationRequested)
            {
                await ExecuteSafelyAsync(job, runJob, cancellationToken);
                await DelayForAsync(interval, cancellationToken);
            }

            return;
        }

        if (job.Schedule is null)
        {
            await ExecuteSafelyAsync(job, runJob, cancellationToken);
            return;
        }

        var expression = ParseSchedule(job.Schedule);
        var timezone = ResolveTimezone(job.Timezone ?? defaultTimezone ?? "UTC");

        await ExecuteSafelyAsync(job, runJob, cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = _timeProvider.GetUtcNow();
            var next = expression.GetNextOccurrence(now, timezone);
            if (next is null)
            {
                _logger.LogWarning("cron expression has no future occurrence: job={Job}", job.Name);
                return;
            }

            await DelayUntilAsync(next.Value, cancellationToken);
            await ExecuteSafelyAsync(job, runJob, cancellationToken);
        }
    }

    private async Task ExecuteSafelyAsync(
        JobConfig job,
        Func<JobConfig, CancellationToken, Task> runJob,
        CancellationToken cancellationToken)
    {
        try
        {
            await runJob(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "scheduled job failed: job={Job}", job.Name);
        }
    }

    private async Task DelayUntilAsync(DateTimeOffset target, CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = target - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await DelayForAsync(remaining, cancellationToken);
        }
    }

    private async Task DelayForAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        // TimeProvider-backed Task.Delay rejects delays outside the platform timer range.
        var chunk = delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay;
        await Task.Delay(chunk, _timeProvider, cancellationToken);
    }

    private static CronExpression ParseSchedule(string schedule)
    {
        if (CronExpression.TryParse(schedule, CronFormat.Standard, out var expression))
        {
            return expression;
        }

        if (CronExpression.TryParse(schedule, CronFormat.IncludeSeconds, out expression))
        {
            return expression;
        }

        throw new ConfigException("schedule", $"invalid cron expression '{schedule}'");
    }

    private static TimeZoneInfo ResolveTimezone(string timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new ConfigException("timezone", $"unknown timezone '{timezone}'");
        }
        catch (InvalidTimeZoneException)
        {
            throw new ConfigException("timezone", $"invalid timezone '{timezone}'");
        }
    }
}
