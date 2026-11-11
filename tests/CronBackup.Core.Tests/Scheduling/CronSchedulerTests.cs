using CronBackup.Core.Configuration;
using CronBackup.Core.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;

namespace CronBackup.Core.Tests.Scheduling;

public sealed class CronSchedulerTests
{
    [Fact]
    public async Task RunAsync_ShouldRunJobOnce_WhenNoScheduleIsConfigured()
    {
        var runs = 0;
        var config = CreateConfig(new JobConfig { Name = "once" });
        var scheduler = new CronScheduler(NullLogger<CronScheduler>.Instance);

        await scheduler.RunAsync(
            config,
            (_, _) =>
            {
                runs++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task RunAsync_ShouldRespectMaxParallel()
    {
        var active = 0;
        var maxActive = 0;
        var config = new RootConfig
        {
            Defaults = new DefaultsConfig { MaxParallel = 1 },
            Jobs =
            [
                new JobConfig { Name = "one" },
                new JobConfig { Name = "two" },
            ],
        };
        var scheduler = new CronScheduler(NullLogger<CronScheduler>.Instance);

        await scheduler.RunAsync(
            config,
            async (_, token) =>
            {
                var current = Interlocked.Increment(ref active);
                if (current > Volatile.Read(ref maxActive))
                {
                    Interlocked.Exchange(ref maxActive, current);
                }

                await Task.Delay(50, token);
                Interlocked.Decrement(ref active);
            },
            CancellationToken.None);

        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task RunAsync_ShouldRunImmediatelyAndStopOnCancellation()
    {
        var runs = 0;
        var config = CreateConfig(new JobConfig
        {
            Name = "scheduled",
            Schedule = "0 0 1 1 *",
        });
        var scheduler = new CronScheduler(NullLogger<CronScheduler>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await scheduler.RunAsync(
            config,
            (_, _) =>
            {
                runs++;
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task RunAsync_ShouldHonorInterval()
    {
        var runs = 0;
        var config = CreateConfig(new JobConfig
        {
            Name = "interval",
            IntervalSeconds = 1,
        });
        var scheduler = new CronScheduler(NullLogger<CronScheduler>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));

        await scheduler.RunAsync(
            config,
            (_, _) =>
            {
                Interlocked.Increment(ref runs);
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.True(runs >= 2, $"expected at least two runs, got {runs}");
    }

    private static RootConfig CreateConfig(JobConfig job) => new()
    {
        Jobs = [job],
    };
}
