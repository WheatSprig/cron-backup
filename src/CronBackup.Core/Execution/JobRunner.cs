using CronBackup.Core.Configuration;
using CronBackup.Core.Retention;
using CronBackup.Core.Storage;
using Microsoft.Extensions.Logging;

namespace CronBackup.Core.Execution;

public sealed record JobRunOptions(bool DryRun = false);

public enum JobRunStatus
{
    Success,
    Failed,
    Locked,
    Skipped,
}

public sealed record DestinationRunResult(
    string Name,
    bool Success,
    IReadOnlyList<string> Uploaded,
    IReadOnlyList<string> Promoted,
    IReadOnlyList<string> Deleted,
    string? Error);

public sealed record JobRunResult(
    string JobName,
    JobRunStatus Status,
    string? ArchiveName,
    IReadOnlyList<DestinationRunResult> Destinations,
    string? Error)
{
    public bool Succeeded => Status == JobRunStatus.Success;
}

public sealed class JobRunner
{
    private readonly IArchiveService _archiveService;
    private readonly IStorageBackendFactory _storageFactory;
    private readonly IJobLockFactory _lockFactory;
    private readonly IHookRunner _hookRunner;
    private readonly INotifier _notifier;
    private readonly ILogger<JobRunner> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly RetryPolicy _retryPolicy;

    public JobRunner(
        IArchiveService archiveService,
        IStorageBackendFactory storageFactory,
        IJobLockFactory lockFactory,
        IHookRunner hookRunner,
        INotifier notifier,
        ILogger<JobRunner> logger,
        TimeProvider? timeProvider = null,
        RetryPolicy? retryPolicy = null)
    {
        _archiveService = archiveService;
        _storageFactory = storageFactory;
        _lockFactory = lockFactory;
        _hookRunner = hookRunner;
        _notifier = notifier;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retryPolicy = retryPolicy ?? new RetryPolicy();
    }

    public async Task<JobRunResult> RunAsync(
        RootConfig config,
        JobConfig job,
        JobRunOptions options,
        CancellationToken cancellationToken)
    {
        if (!job.IsEnabled)
        {
            return new JobRunResult(job.Name, JobRunStatus.Skipped, null, Array.Empty<DestinationRunResult>(), null);
        }

        await using var lockResult = await _lockFactory.TryAcquireAsync(job.Name, cancellationToken);
        if (!lockResult.Acquired)
        {
            _logger.LogWarning("job is already running: {Job}", job.Name);
            return new JobRunResult(job.Name, JobRunStatus.Locked, null, Array.Empty<DestinationRunResult>(), "job is already running");
        }

        var dryRun = options.DryRun || config.Defaults.DryRun == true;
        if (dryRun)
        {
            _logger.LogInformation("dry-run enabled, skipping archive and remote operations: {Job}", job.Name);
            return new JobRunResult(job.Name, JobRunStatus.Success, null, Array.Empty<DestinationRunResult>(), null);
        }

        var timezone = ResolveTimezone(job.Timezone ?? config.Defaults.Timezone ?? "UTC");
        var now = _timeProvider.GetUtcNow();
        var localNow = TimeZoneInfo.ConvertTime(now, timezone);
        var hooks = job.Hooks;
        var hookTimeout = TimeSpan.FromSeconds(hooks?.TimeoutSeconds ?? 300);
        var notifyTargets = job.Notify ?? config.Defaults.Notify;
        var archiveOptions = job.Archive ?? config.Defaults.Archive;
        var tempDirectory = string.IsNullOrWhiteSpace(archiveOptions?.TempDir)
            ? Path.GetTempPath()
            : archiveOptions!.TempDir!;

        var failures = new List<string>();
        var destinationResults = new List<DestinationRunResult>();
        ArchiveResult? archive = null;
        var archiveName = string.Empty;
        var postContext = new HookContext(job.Name, job.Source.Path, null, dryRun, hookTimeout);

        try
        {
            await NotifyAsync(notifyTargets, NotificationPhase.Start, job, false, string.Empty, now, cancellationToken);

            if (hooks?.Pre is { Count: > 0 })
            {
                var preResult = await _hookRunner.RunAsync(
                    HookPhase.Pre,
                    hooks.Pre,
                    new HookContext(job.Name, job.Source.Path, null, dryRun, hookTimeout),
                    stopOnFailure: true,
                    cancellationToken);

                if (!preResult.Succeeded)
                {
                    failures.Add("pre hook failed");
                }
            }

            if (failures.Count == 0)
            {
                archive = await _archiveService.CreateAsync(
                    new ArchiveRequest(job.Name, job.Source.Path, tempDirectory, localNow),
                    cancellationToken);
                archiveName = archive.FileName;
                postContext = postContext with { ArchivePath = archive.ArchivePath };

                foreach (var destination in job.Destinations)
                {
                    destinationResults.Add(await RunDestinationAsync(
                        config,
                        job,
                        destination,
                        archive,
                        now,
                        timezone,
                        cancellationToken));

                    if (!destinationResults[^1].Success)
                    {
                        failures.Add($"{destination.Name}: {destinationResults[^1].Error}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "job failed: {Job}", job.Name);
            failures.Add(exception.Message);
        }
        finally
        {
            if (hooks?.Post is { Count: > 0 })
            {
                var postResult = await _hookRunner.RunAsync(
                    HookPhase.Post,
                    hooks.Post,
                    postContext,
                    stopOnFailure: false,
                    cancellationToken);
                if (!postResult.Succeeded)
                {
                    failures.Add("post hook failed");
                }
            }

            if (archive is not null)
            {
                await archive.DisposeAsync();
            }
        }

        var success = failures.Count == 0 &&
                      destinationResults.Count > 0 &&
                      destinationResults.All(result => result.Success);
        var error = failures.Count == 0 ? null : string.Join("; ", failures);

        await NotifyAsync(
            notifyTargets,
            success ? NotificationPhase.Success : NotificationPhase.Failure,
            job,
            success,
            error ?? string.Empty,
            now,
            cancellationToken,
            archiveName,
            destinationResults.Select(result => result.Name).ToList());

        return new JobRunResult(
            job.Name,
            success ? JobRunStatus.Success : JobRunStatus.Failed,
            archiveName,
            destinationResults,
            error);
    }

    private async Task<DestinationRunResult> RunDestinationAsync(
        RootConfig config,
        JobConfig job,
        DestinationConfig destination,
        ArchiveResult archive,
        DateTimeOffset now,
        TimeZoneInfo timezone,
        CancellationToken cancellationToken)
    {
        var uploaded = new List<string>();
        var promoted = new List<string>();
        var deleted = new List<string>();

        try
        {
            await using var backend = await _storageFactory.CreateAsync(job, destination, cancellationToken);
            var key = $"daily/{archive.FileName}";

            await _retryPolicy.ExecuteAsync(
                token => backend.UploadAsync(archive.ArchivePath, key, token),
                ShouldRetry,
                cancellationToken);
            uploaded.Add(key);
            await VerifyAsync(backend, key, archive.Size, job.Verify ?? "size", cancellationToken);

            var policy = RetentionResolver.Resolve(
                config.Defaults.Retention,
                job.Retention,
                destination.Retention);

            if (policy.Enabled)
            {
                var daily = await backend.ListAsync("daily", cancellationToken);
                var weekly = await backend.ListAsync("weekly", cancellationToken);
                var monthly = await backend.ListAsync("monthly", cancellationToken);
                var promotion = RetentionPlanner.BuildPlan(
                    policy,
                    daily,
                    weekly,
                    monthly,
                    now,
                    timezone,
                    key);

                if (promotion.Promotion.Weekly)
                {
                    var promotedKey = await PromoteAsync(
                        backend,
                        "weekly",
                        archive,
                        cancellationToken);
                    if (promotedKey is not null)
                    {
                        promoted.Add(promotedKey);
                    }
                }

                if (promotion.Promotion.Monthly)
                {
                    var promotedKey = await PromoteAsync(
                        backend,
                        "monthly",
                        archive,
                        cancellationToken);
                    if (promotedKey is not null)
                    {
                        promoted.Add(promotedKey);
                    }
                }

                daily = await backend.ListAsync("daily", cancellationToken);
                weekly = await backend.ListAsync("weekly", cancellationToken);
                monthly = await backend.ListAsync("monthly", cancellationToken);
                var prune = RetentionPlanner.BuildPlan(
                    policy,
                    daily,
                    weekly,
                    monthly,
                    now,
                    timezone,
                    key);

                if (prune.Prune.Any(plan => plan.Aborted))
                {
                    _logger.LogWarning(
                        "retention delete limit exceeded, skipping deletion: job={Job} destination={Destination}",
                        job.Name,
                        destination.Name);
                }
                else
                {
                    var toDelete = prune.Prune
                        .SelectMany(plan => plan.ToDelete)
                        .Select(item => item.Key)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    if (toDelete.Count > 0)
                    {
                        await _retryPolicy.ExecuteAsync(
                            token => backend.DeleteManyAsync(toDelete, token),
                            ShouldRetry,
                            cancellationToken);
                        deleted.AddRange(toDelete);
                    }
                }
            }

            return new DestinationRunResult(
                destination.Name,
                true,
                uploaded,
                promoted,
                deleted,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "destination failed: job={Job} destination={Destination}",
                job.Name,
                destination.Name);
            return new DestinationRunResult(
                destination.Name,
                false,
                uploaded,
                promoted,
                deleted,
                exception.Message);
        }
    }

    private async Task VerifyAsync(
        IStorageBackend backend,
        string key,
        long expectedSize,
        string verifyMode,
        CancellationToken cancellationToken)
    {
        if (string.Equals(verifyMode, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(verifyMode, "checksum", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("checksum verification is not available in this build yet");
        }

        var stat = await backend.StatAsync(key, cancellationToken);
        if (stat is null)
        {
            throw new InvalidOperationException($"uploaded object not found after upload: {key}");
        }

        if (stat.Size != expectedSize)
        {
            throw new InvalidOperationException(
                $"uploaded object size mismatch: {key} expected {expectedSize}, got {stat.Size}");
        }
    }

    private async Task<string?> PromoteAsync(
        IStorageBackend backend,
        string tier,
        ArchiveResult archive,
        CancellationToken cancellationToken)
    {
        var targetKey = $"{tier}/{archive.FileName}";
        var existing = await backend.StatAsync(targetKey, cancellationToken);
        if (existing is not null)
        {
            return null;
        }

        if (backend.SupportsServerSideCopy)
        {
            await _retryPolicy.ExecuteAsync(
                token => backend.CopyWithinAsync($"daily/{archive.FileName}", targetKey, token),
                ShouldRetry,
                cancellationToken);
        }
        else
        {
            var temporaryPath = Path.Combine(
                archive.DirectoryPath,
                $".promote-{Guid.NewGuid():N}");
            try
            {
                await _retryPolicy.ExecuteAsync(
                    token => backend.DownloadAsync($"daily/{archive.FileName}", temporaryPath, token),
                    ShouldRetry,
                    cancellationToken);
                await _retryPolicy.ExecuteAsync(
                    token => backend.UploadAsync(temporaryPath, targetKey, token),
                    ShouldRetry,
                    cancellationToken);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        await VerifyAsync(backend, targetKey, archive.Size, "size", cancellationToken);
        return targetKey;
    }

    private async Task NotifyAsync(
        IReadOnlyList<NotifyConfig>? targets,
        NotificationPhase phase,
        JobConfig job,
        bool success,
        string error,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? archiveName = null,
        IReadOnlyList<string>? destinations = null)
    {
        try
        {
            var results = await _notifier.SendAsync(
                targets,
                phase,
                new NotificationMessage(
                    job.Name,
                    success,
                    archiveName ?? string.Empty,
                    destinations ?? job.Destinations.Select(destination => destination.Name).ToList(),
                    error,
                    now),
                cancellationToken);

            foreach (var result in results.Where(result => !result.Success))
            {
                _logger.LogWarning(
                    "notification failed: job={Job} kind={Kind} error={Error}",
                    job.Name,
                    result.Kind,
                    result.Error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "notification failed: job={Job} phase={Phase}", job.Name, phase);
        }
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

    private static bool ShouldRetry(Exception exception) =>
        exception is IOException or HttpRequestException or TaskCanceledException or ProcessExecutionException;
}
