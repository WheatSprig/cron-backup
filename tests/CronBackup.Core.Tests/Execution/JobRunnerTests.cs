using CronBackup.Core.Configuration;
using CronBackup.Core.Execution;
using CronBackup.Core.Storage;
using CronBackup.Core.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CronBackup.Core.Tests.Execution;

public sealed class JobRunnerTests
{
    private static readonly DateTimeOffset Sunday =
        new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunAsync_ShouldArchiveUploadAndNotify()
    {
        using var temp = new TempDirectory();
        var (runner, archive, notifier) = CreateRunner(temp);
        var config = CreateConfig(temp, retention: new RetentionPolicy { Enabled = false });

        var result = await runner.RunAsync(
            config,
            config.Jobs[0],
            new JobRunOptions(),
            CancellationToken.None);

        Assert.Equal(JobRunStatus.Success, result.Status);
        Assert.Single(archive.Requests);
        var destination = Assert.Single(result.Destinations);
        Assert.True(destination.Success);
        Assert.Single(destination.Uploaded);
        Assert.Contains(notifier.Calls, call => call.Phase == NotificationPhase.Start);
        Assert.Contains(notifier.Calls, call => call.Phase == NotificationPhase.Success);

        var remote = Path.Combine(temp.Path, "remote");
        Assert.Single(Directory.GetFiles(remote, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RunAsync_ShouldPruneOldDailyObjects()
    {
        using var temp = new TempDirectory();
        var remote = Path.Combine(temp.Path, "remote");
        Directory.CreateDirectory(Path.Combine(remote, "daily"));
        var oldPath = Path.Combine(remote, "daily", "backup_old.tar.gz");
        await File.WriteAllTextAsync(oldPath, "old");
        File.SetLastWriteTimeUtc(oldPath, Sunday.AddDays(-10).UtcDateTime);

        var (runner, _, _) = CreateRunner(temp);
        var config = CreateConfig(
            temp,
            new RetentionPolicy { Daily = 1, MaxDelete = 20 });

        var result = await runner.RunAsync(
            config,
            config.Jobs[0],
            new JobRunOptions(),
            CancellationToken.None);

        Assert.Equal(JobRunStatus.Success, result.Status);
        var daily = Directory.GetFiles(Path.Combine(remote, "daily"));
        Assert.Single(daily);
        Assert.DoesNotContain("backup_old.tar.gz", daily[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldPromoteWeeklyOnSunday()
    {
        using var temp = new TempDirectory();
        var (runner, _, _) = CreateRunner(temp);
        var config = CreateConfig(
            temp,
            new RetentionPolicy { Daily = 7, Weekly = 1, MaxDelete = 20 });

        var result = await runner.RunAsync(
            config,
            config.Jobs[0],
            new JobRunOptions(),
            CancellationToken.None);

        Assert.Equal(JobRunStatus.Success, result.Status);
        var destination = Assert.Single(result.Destinations);
        Assert.Single(destination.Promoted);
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "remote", "weekly")));
        Assert.Single(Directory.GetFiles(Path.Combine(temp.Path, "remote", "weekly")));
    }

    [Fact]
    public async Task RunAsync_ShouldStopBeforeArchive_WhenPreHookFails()
    {
        using var temp = new TempDirectory();
        var processRunner = new StubProcessRunner(_ => new ProcessResult(1, "", "failed", false));
        var (runner, archive, _) = CreateRunner(temp, processRunner);
        var config = CreateConfig(temp, new RetentionPolicy { Enabled = false });
        config.Jobs[0].Hooks = new HooksConfig
        {
            Pre = [new HookDefinition { Run = "/bin/fail" }],
        };

        var result = await runner.RunAsync(
            config,
            config.Jobs[0],
            new JobRunOptions(),
            CancellationToken.None);

        Assert.Equal(JobRunStatus.Failed, result.Status);
        Assert.Empty(archive.Requests);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "remote")));
    }

    [Fact]
    public async Task RunAsync_ShouldReturnLocked_WhenJobIsAlreadyRunning()
    {
        using var temp = new TempDirectory();
        var lockFactory = new FileJobLockFactory(Path.Combine(temp.Path, "locks"));
        var processRunner = new StubProcessRunner(_ => new ProcessResult(0, "", "", false));
        var runner = new JobRunner(
            new FakeArchiveService(),
            new DefaultStorageBackendFactory(),
            lockFactory,
            new HookRunner(processRunner, NullLogger<HookRunner>.Instance),
            new RecordingNotifier(),
            NullLogger<JobRunner>.Instance,
            new FixedTimeProvider(Sunday),
            new RetryPolicy(new RetryOptions(1, TimeSpan.Zero, TimeSpan.Zero, 0)));
        var config = CreateConfig(temp, new RetentionPolicy { Enabled = false });

        await using var existing = await lockFactory.TryAcquireAsync(config.Jobs[0].Name, CancellationToken.None);
        var result = await runner.RunAsync(
            config,
            config.Jobs[0],
            new JobRunOptions(),
            CancellationToken.None);

        Assert.Equal(JobRunStatus.Locked, result.Status);
        Assert.True(existing.Acquired);
    }

    private static (JobRunner Runner, FakeArchiveService Archive, RecordingNotifier Notifier) CreateRunner(
        TempDirectory temp,
        StubProcessRunner? processRunner = null)
    {
        var archive = new FakeArchiveService();
        var notifier = new RecordingNotifier();
        var runner = new JobRunner(
            archive,
            new DefaultStorageBackendFactory(),
            new FileJobLockFactory(Path.Combine(temp.Path, "locks")),
            new HookRunner(
                processRunner ?? new StubProcessRunner(_ => new ProcessResult(0, "", "", false)),
                NullLogger<HookRunner>.Instance),
            notifier,
            NullLogger<JobRunner>.Instance,
            new FixedTimeProvider(Sunday),
            new RetryPolicy(new RetryOptions(1, TimeSpan.Zero, TimeSpan.Zero, 0)));

        return (runner, archive, notifier);
    }

    private static RootConfig CreateConfig(TempDirectory temp, RetentionPolicy retention)
    {
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "data.txt"), "data");

        return new RootConfig
        {
            Defaults = new DefaultsConfig
            {
                Retention = retention,
            },
            Jobs =
            [
                new JobConfig
                {
                    Name = "photos",
                    Schedule = "0 2 * * *",
                    Source = new SourceConfig { Path = source },
                    Verify = "size",
                    Destinations =
                    [
                        new DestinationConfig
                        {
                            Name = "local",
                            Kind = "local",
                            Path = Path.Combine(temp.Path, "remote"),
                        },
                    ],
                },
            ],
        };
    }
}
