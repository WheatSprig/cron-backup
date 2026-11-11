using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CronBackup.Cli.Logging;
using CronBackup.Core.Configuration;
using CronBackup.Core.Execution;
using CronBackup.Core.Health;
using CronBackup.Core.Rclone;
using CronBackup.Core.Retention;
using CronBackup.Core.Scheduling;
using CronBackup.Core.Storage;
using Microsoft.Extensions.Logging;

namespace CronBackup.Cli;

public static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (CliUsageException exception)
        {
            await standardError.WriteLineAsync($"error: {exception.Message}");
            await WriteUsageAsync(standardError);
            return 1;
        }

        if (options.Help)
        {
            await WriteUsageAsync(standardOutput);
            return 0;
        }

        TempWorkspace.CleanupStale(TimeSpan.FromHours(24));

        try
        {
            return options.Command switch
            {
                "help" => await HelpAsync(standardOutput),
                "version" => await VersionAsync(options, standardOutput),
                "validate" => await ValidateAsync(options, standardOutput, standardError, cancellationToken),
                "plan" => await PlanAsync(options, standardOutput, standardError, cancellationToken),
                "list" => await ListAsync(options, standardOutput, standardError, cancellationToken),
                "run" => await RunCommandAsync(options, standardOutput, standardError, cancellationToken),
                "prune" => await PruneCommandAsync(options, standardOutput, standardError, cancellationToken),
                "restore" => await RestoreCommandAsync(options, standardOutput, standardError, cancellationToken),
                "health" => await HealthCommandAsync(options, standardOutput, standardError),
                _ => await UnknownCommandAsync(options.Command, standardError),
            };
        }
        catch (ConfigException exception)
        {
            await standardError.WriteLineAsync($"config error: {exception.Message}");
            return 2;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception)
        {
            await standardError.WriteLineAsync($"error: {exception.Message}");
            return 3;
        }
    }

    private static async Task<int> HelpAsync(TextWriter output)
    {
        await WriteUsageAsync(output);
        return 0;
    }

    private static async Task<int> VersionAsync(CliOptions options, TextWriter output)
    {
        var assembly = typeof(CliApplication).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        if (options.Json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                name = "cron-backup",
                version,
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
            }, JsonOptions));
        }
        else
        {
            await output.WriteLineAsync($"cron-backup {version}");
            await output.WriteLineAsync(RuntimeInformation.FrameworkDescription);
            await output.WriteLineAsync(RuntimeInformation.OSDescription);
        }

        return 0;
    }

    private static async Task<int> ValidateAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var loaded = Load(options);
        var result = new ConfigValidator().Validate(loaded.Config);
        WriteWarnings(loaded, result, error);

        foreach (var validationError in result.Errors)
        {
            await error.WriteLineAsync($"error: {validationError.Path}: {validationError.Message}");
        }

        if (!result.IsValid)
        {
            return 2;
        }

        if (options.Online)
        {
            await error.WriteLineAsync("warning: --online checks are not implemented in this build");
        }

        if (options.Json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                valid = true,
                source = DescribeSource(loaded),
                jobs = loaded.Config.Jobs.Count,
                warnings = result.Warnings.Select(item => new { path = item.Path, message = item.Message }),
            }, JsonOptions));
        }
        else
        {
            await output.WriteLineAsync($"valid: {DescribeSource(loaded)}");
            await output.WriteLineAsync($"jobs: {loaded.Config.Jobs.Count}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }

    private static async Task<int> PlanAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var loaded = Load(options);
        var validation = new ConfigValidator().Validate(loaded.Config);
        WriteWarnings(loaded, validation, error);

        if (!validation.IsValid)
        {
            foreach (var validationError in validation.Errors)
            {
                await error.WriteLineAsync($"error: {validationError.Path}: {validationError.Message}");
            }

            return 2;
        }

        var jobs = FilterJobs(loaded.Config, options.Job);
        if (jobs.Count == 0)
        {
            await error.WriteLineAsync($"error: job '{options.Job}' not found");
            return 2;
        }

        if (options.Json)
        {
            var jobModels = new List<object>();
            foreach (var job in jobs)
            {
                jobModels.Add(BuildJobPlanModel(loaded.Config, job));
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                source = DescribeSource(loaded),
                dryRun = options.DryRun || loaded.Config.Defaults.DryRun == true,
                jobs = jobModels,
            }, JsonOptions));
        }
        else
        {
            await output.WriteLineAsync($"config: {DescribeSource(loaded)}");
            await output.WriteLineAsync($"dry-run: {(options.DryRun || loaded.Config.Defaults.DryRun == true ? "yes" : "no")}");
            await output.WriteLineAsync();

            foreach (var job in jobs)
            {
                WriteJobPlan(output, loaded.Config, job);
                await output.WriteLineAsync();
            }

            await output.WriteLineAsync("plan is offline: no remote objects were listed and no hooks were executed");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }

    private static async Task<int> ListAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var loaded = Load(options);
        var validation = new ConfigValidator().Validate(loaded.Config);
        WriteWarnings(loaded, validation, error);

        if (!validation.IsValid)
        {
            foreach (var validationError in validation.Errors)
            {
                await error.WriteLineAsync($"error: {validationError.Path}: {validationError.Message}");
            }

            return 2;
        }

        var jobs = FilterJobs(loaded.Config, options.Job);
        if (jobs.Count == 0)
        {
            await error.WriteLineAsync($"error: job '{options.Job}' not found");
            return 2;
        }

        if (options.Remote)
        {
            return await ListRemoteAsync(
                loaded.Config,
                jobs,
                options,
                output,
                error,
                cancellationToken);
        }

        if (options.Json)
        {
            var jobModels = jobs.Select(job => new
            {
                name = job.Name,
                enabled = job.IsEnabled,
                schedule = job.Schedule,
                intervalSeconds = job.IntervalSeconds,
                source = job.Source.Path,
                destinations = job.Destinations.Select(destination => new
                {
                    name = destination.Name,
                    kind = destination.Kind,
                    root = destination.Root,
                    bucket = destination.Bucket,
                }),
            });

            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                source = DescribeSource(loaded),
                jobs = jobModels,
            }, JsonOptions));
        }
        else
        {
            await output.WriteLineAsync($"config: {DescribeSource(loaded)}");
            foreach (var job in jobs)
            {
                var schedule = job.Schedule ?? (job.IntervalSeconds is null
                    ? "once"
                    : $"every {job.IntervalSeconds}s");
                await output.WriteLineAsync($"{job.Name} [{schedule}] {job.Source.Path}");
                foreach (var destination in job.Destinations)
                {
                    var location = destination.Kind.Equals("s3", StringComparison.OrdinalIgnoreCase)
                        ? $"{destination.Bucket}/{destination.Root}".TrimEnd('/')
                        : destination.Root;
                    await output.WriteLineAsync($"  - {destination.Name} ({destination.Kind}) {location}");
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }

    private static async Task<int> RunCommandAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var loaded = Load(options);
        var validation = new ConfigValidator().Validate(loaded.Config);
        WriteWarnings(loaded, validation, error);

        if (!validation.IsValid)
        {
            foreach (var validationError in validation.Errors)
            {
                await error.WriteLineAsync($"error: {validationError.Path}: {validationError.Message}");
            }

            return 2;
        }

        var jobs = FilterJobs(loaded.Config, options.Job);
        if (jobs.Count == 0)
        {
            await error.WriteLineAsync($"error: job '{options.Job}' not found");
            return 2;
        }

        var runOnce = options.Once ||
                      options.DryRun ||
                      jobs.All(job => job.Schedule is null && job.IntervalSeconds is null);

        var logLevel = ParseLogLevel(options.LogLevel);
        var logTarget = loaded.Config.Defaults.LogTarget ??
                        Environment.GetEnvironmentVariable("LOG_TARGET") ??
                        "console";
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(logLevel);
            if (string.Equals(logTarget, "file", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var maxSizeMb = long.TryParse(
                        Environment.GetEnvironmentVariable("LOG_MAX_SIZE_MB"),
                        out var parsedSize)
                        ? parsedSize
                        : 50;
                    var maxFiles = int.TryParse(
                        Environment.GetEnvironmentVariable("LOG_MAX_FILES"),
                        out var parsedFiles)
                        ? parsedFiles
                        : 3;
                    builder.AddProvider(new FileLoggerProvider(
                        Environment.GetEnvironmentVariable("LOG_FILE") ?? "/var/log/cron-backup/backup.log",
                        logLevel,
                        maxSizeMb * 1024 * 1024,
                        maxFiles));
                    return;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            builder.AddConsole(console =>
            {
                console.LogToStandardErrorThreshold = LogLevel.Trace;
            });
        });

        using var httpClient = new HttpClient();
        var processRunner = new SystemProcessRunner();
        var storageFactory = new RcloneStorageBackendFactory(
            new RcloneConfRenderer(),
            new RcloneProcessSecretObscurer(),
            processRunner);
        var healthStore = new FileHealthStore(
            Environment.GetEnvironmentVariable("CRONBACKUP_STATUS_FILE"));
        var runner = new JobRunner(
            new TarArchiveService(processRunner),
            storageFactory,
            CreateLockFactory(),
            new HookRunner(processRunner, loggerFactory.CreateLogger<HookRunner>()),
            new HttpNotifier(httpClient),
            loggerFactory.CreateLogger<JobRunner>(),
            healthStore: healthStore);

        if (runOnce)
        {
            var results = new List<JobRunResult>();
            foreach (var job in jobs)
            {
                results.Add(await runner.RunAsync(
                    loaded.Config,
                    job,
                    new JobRunOptions(options.DryRun),
                    cancellationToken));
            }

            await WriteRunResultsAsync(options, loaded.Config, results, output);
            if (results.Any(result => result.Status == JobRunStatus.Locked))
            {
                return 4;
            }

            return results.All(result => result.Status is JobRunStatus.Success or JobRunStatus.Skipped) ? 0 : 3;
        }

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        PosixSignalRegistration? sigterm = null;
        PosixSignalRegistration? sigint = null;
        if (!OperatingSystem.IsWindows())
        {
            sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
            });
            sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
            });
        }

        try
        {
            var schedulerConfig = new RootConfig
            {
                Version = loaded.Config.Version,
                Defaults = loaded.Config.Defaults,
                Jobs = jobs,
            };
            var scheduler = new CronScheduler(
                loggerFactory.CreateLogger<CronScheduler>(),
                healthStore);
            await output.WriteLineAsync("scheduler started");
            await scheduler.RunAsync(
                schedulerConfig,
                async (job, token) =>
                {
                    await runner.RunAsync(
                        schedulerConfig,
                        job,
                        new JobRunOptions(options.DryRun),
                        token);
                },
                shutdown.Token);
            await output.WriteLineAsync("scheduler stopped");
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            sigterm?.Dispose();
            sigint?.Dispose();
        }

        return 0;
    }

    private static async Task WriteRunResultsAsync(
        CliOptions options,
        RootConfig config,
        IReadOnlyList<JobRunResult> results,
        TextWriter output)
    {
        if (options.Json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                dryRun = options.DryRun || config.Defaults.DryRun == true,
                jobs = results.Select(result => new
                {
                    name = result.JobName,
                    status = result.Status.ToString().ToLowerInvariant(),
                    archive = result.ArchiveName,
                    error = result.Error,
                    pendingDeletes = result.PendingDeletes,
                    destinations = result.Destinations.Select(destination => new
                    {
                        name = destination.Name,
                        success = destination.Success,
                        uploaded = destination.Uploaded,
                        promoted = destination.Promoted,
                        deleted = destination.Deleted,
                        pendingDeletes = destination.PendingDeletes,
                        error = destination.Error,
                    }),
                }),
            }, JsonOptions));
            return;
        }

        foreach (var result in results)
        {
            await output.WriteLineAsync($"{result.JobName}: {result.Status.ToString().ToLowerInvariant()}");
            if (!string.IsNullOrEmpty(result.ArchiveName))
            {
                await output.WriteLineAsync($"  archive: {result.ArchiveName}");
            }

            foreach (var destination in result.Destinations)
            {
                await output.WriteLineAsync(
                    $"  {destination.Name}: {(destination.Success ? "ok" : "failed")} " +
                    $"uploaded={destination.Uploaded.Count} promoted={destination.Promoted.Count} deleted={destination.Deleted.Count}");
                if (destination.PendingDeletes > 0)
                {
                    await output.WriteLineAsync(
                        $"    retention: {destination.PendingDeletes} more object(s) deferred by max_delete, will retry next run");
                }

                if (destination.Error is not null)
                {
                    await output.WriteLineAsync($"    error: {destination.Error}");
                }
            }

            if (result.Error is not null && result.Destinations.All(destination => destination.Error is null))
            {
                await output.WriteLineAsync($"  error: {result.Error}");
            }
        }
    }

    // 手动 prune 默认 dry-run；只有 --apply 且没有 DRY_RUN 时才真正删除。
    // max_delete 超限时本轮只删上限数量，剩余在输出里报告，下次继续。
    private static async Task<int> PruneCommandAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var loaded = Load(options);
        var validation = new ConfigValidator().Validate(loaded.Config);
        WriteWarnings(loaded, validation, error);
        if (!validation.IsValid)
        {
            foreach (var validationError in validation.Errors)
            {
                await error.WriteLineAsync($"error: {validationError.Path}: {validationError.Message}");
            }

            return 2;
        }

        var jobs = FilterJobs(loaded.Config, options.Job);
        if (jobs.Count == 0)
        {
            await error.WriteLineAsync($"error: job '{options.Job}' not found");
            return 2;
        }

        var apply = options.Apply && !options.DryRun && loaded.Config.Defaults.DryRun != true;
        var now = DateTimeOffset.UtcNow;
        var factory = CreateStorageFactory();
        var lockFactory = CreateLockFactory();
        var results = new List<object>();
        var failures = new List<string>();

        foreach (var job in jobs)
        {
            // prune 是破坏性操作，必须和 run 共用同一把 job 锁。
            // 跨容器执行时要把 CRONBACKUP_LOCK_DIR 指到共享可写卷，否则锁不互通。
            await using var jobLock = await lockFactory.TryAcquireAsync(job.Name, cancellationToken);
            if (!jobLock.Acquired)
            {
                failures.Add($"{job.Name}: job is already running");
                continue;
            }

            var timezone = ResolveTimezone(job.Timezone ?? loaded.Config.Defaults.Timezone ?? "UTC");
            foreach (var destination in job.Destinations)
            {
                var policy = RetentionResolver.Resolve(
                    loaded.Config.Defaults.Retention,
                    job.Retention,
                    destination.Retention);
                if (!policy.Enabled)
                {
                    continue;
                }

                await using var backend = await factory.CreateAsync(job, destination, cancellationToken);
                var plan = await RetentionExecutor.BuildPlanAsync(
                    backend,
                    policy,
                    now,
                    timezone,
                    currentKey: null,
                    cancellationToken);

                // 候选超过 max_delete 时只删前 max_delete 个，剩余留给下次运行。
                IReadOnlyList<string> deleted = Array.Empty<string>();
                var pending = plan.PendingDeletes;
                if (apply)
                {
                    var applied = await RetentionExecutor.ApplyAsync(backend, plan, cancellationToken);
                    deleted = applied.Deleted;
                    pending = applied.Pending;
                }

                var candidates = plan.Prune
                    .SelectMany(item => item.Candidates)
                    .Select(item => item.Key)
                    .ToList();

                results.Add(new
                {
                    job = job.Name,
                    destination = destination.Name,
                    apply,
                    truncated = pending > 0,
                    candidates,
                    deleted,
                    pendingDeletes = pending,
                });

                if (options.Json)
                {
                    continue;
                }

                await output.WriteLineAsync(
                    $"{job.Name}/{destination.Name}: candidates={candidates.Count} " +
                    $"deleted={deleted.Count} pending={pending} mode={(apply ? "apply" : "dry-run")}");
                foreach (var key in candidates)
                {
                    await output.WriteLineAsync($"  - {key}");
                }
            }
        }

        foreach (var failure in failures)
        {
            await error.WriteLineAsync($"error: {failure}");
        }

        if (options.Json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                apply,
                failures,
                results,
            }, JsonOptions));
        }

        return failures.Count > 0 ? 3 : 0;
    }

    // restore 只操作一个 destination；--extract 先下载到临时文件，再做防穿越解压。
    private static async Task<int> RestoreCommandAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var loaded = Load(options);
        var validation = new ConfigValidator().Validate(loaded.Config);
        WriteWarnings(loaded, validation, error);
        if (!validation.IsValid)
        {
            foreach (var validationError in validation.Errors)
            {
                await error.WriteLineAsync($"error: {validationError.Path}: {validationError.Message}");
            }

            return 2;
        }

        if (string.IsNullOrWhiteSpace(options.Job))
        {
            await error.WriteLineAsync("error: restore requires --job");
            return 1;
        }

        var job = loaded.Config.FindJob(options.Job);
        if (job is null)
        {
            await error.WriteLineAsync($"error: job '{options.Job}' not found");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(options.To))
        {
            await error.WriteLineAsync("error: restore requires --to");
            return 1;
        }

        if (options.Latest == (options.Key is not null))
        {
            await error.WriteLineAsync("error: restore requires exactly one of --latest or --key");
            return 1;
        }

        var destination = options.Destination is null
            ? job.Destinations.FirstOrDefault()
            : job.Destinations.FirstOrDefault(item =>
                string.Equals(item.Name, options.Destination, StringComparison.Ordinal));
        if (destination is null)
        {
            await error.WriteLineAsync($"error: destination '{options.Destination}' not found");
            return 2;
        }

        var tier = NormalizeTier(options.Tier);
        var factory = CreateStorageFactory();
        await using var backend = await factory.CreateAsync(job, destination, cancellationToken);

        string key;
        if (options.Latest)
        {
            var objects = await backend.ListAsync(tier, cancellationToken);
            var latest = objects.OrderByDescending(item => item.Modified).FirstOrDefault();
            if (latest is null)
            {
                await error.WriteLineAsync($"error: no objects found in {tier}");
                return 3;
            }

            key = latest.Key;
        }
        else
        {
            key = options.Key!.StartsWith($"{tier}/", StringComparison.Ordinal)
                ? options.Key
                : $"{tier}/{options.Key}";
        }

        var destinationRoot = Path.GetFullPath(options.To);
        Directory.CreateDirectory(destinationRoot);

        if (options.Extract)
        {
            // 扩展名只用于临时文件；真正的 gzip 判断在 TarExtractor 里按文件头做。
            var temporaryExtension = key.EndsWith(".tar", StringComparison.Ordinal)
                ? ".tar"
                : ".tar.gz";
            var temporaryPath = Path.Combine(
                Path.GetTempPath(),
                "cron-backup",
                $"restore-{Guid.NewGuid():N}{temporaryExtension}");
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
            try
            {
                await backend.DownloadAsync(key, temporaryPath, cancellationToken);
                await TarExtractor.ExtractAsync(temporaryPath, destinationRoot, cancellationToken);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            await output.WriteLineAsync($"restored {key} to {destinationRoot}");
        }
        else
        {
            var targetPath = Path.Combine(destinationRoot, Path.GetFileName(key));
            await backend.DownloadAsync(key, targetPath, cancellationToken);
            await output.WriteLineAsync($"downloaded {key} to {targetPath}");
        }

        return 0;
    }

    private static async Task<int> ListRemoteAsync(
        RootConfig config,
        IReadOnlyList<JobConfig> jobs,
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var factory = CreateStorageFactory();
        var tiers = options.Tier is null
            ? new[] { "daily", "weekly", "monthly" }
            : new[] { NormalizeTier(options.Tier) };
        var rows = new List<RemoteRow>();

        foreach (var job in jobs)
        {
            foreach (var destination in job.Destinations)
            {
                if (options.Destination is not null &&
                    !string.Equals(destination.Name, options.Destination, StringComparison.Ordinal))
                {
                    continue;
                }

                await using var backend = await factory.CreateAsync(job, destination, cancellationToken);
                foreach (var tier in tiers)
                {
                    var objects = await backend.ListAsync(tier, cancellationToken);
                    rows.AddRange(objects.Select(item => new RemoteRow(
                        job.Name,
                        destination.Name,
                        tier,
                        item.Key,
                        item.Size,
                        item.Modified)));
                }
            }
        }

        if (options.Json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new { objects = rows }, JsonOptions));
            return 0;
        }

        if (rows.Count == 0)
        {
            await output.WriteLineAsync("no remote objects found");
            return 0;
        }

        foreach (var row in rows)
        {
            await output.WriteLineAsync(
                $"{row.Job}/{row.Destination} {row.Tier} {row.Key} {row.Size} {row.Modified:O}");
        }

        return 0;
    }

    private sealed record RemoteRow(
        string Job,
        string Destination,
        string Tier,
        string Key,
        long Size,
        DateTimeOffset Modified);

    private static RcloneStorageBackendFactory CreateStorageFactory()
    {
        var processRunner = new SystemProcessRunner();
        return new RcloneStorageBackendFactory(
            new RcloneConfRenderer(),
            new RcloneProcessSecretObscurer(),
            processRunner);
    }

    private static FileJobLockFactory CreateLockFactory()
    {
        var directory = Environment.GetEnvironmentVariable("CRONBACKUP_LOCK_DIR");
        return new FileJobLockFactory(string.IsNullOrWhiteSpace(directory) ? null : directory);
    }

    private static string NormalizeTier(string? tier)
    {
        var normalized = (tier ?? "daily").ToLowerInvariant();
        return normalized is "daily" or "weekly" or "monthly"
            ? normalized
            : throw new ConfigException("--tier", "expected daily, weekly or monthly");
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

    // Docker healthcheck 只判断守护进程/调度器是否活着；备份失败交给通知。
    // --strict 才额外把最近一次失败或连续失败计入 unhealthy。
    private static async Task<int> HealthCommandAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error)
    {
        var store = new FileHealthStore(
            Environment.GetEnvironmentVariable("CRONBACKUP_STATUS_FILE"));
        var state = await store.ReadAsync(CancellationToken.None);
        var result = HealthEvaluator.Evaluate(
            state,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(90),
            options.Strict);

        if (options.Json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                healthy = result.Healthy,
                statusFile = store.FilePath,
                errors = result.Errors,
            }, JsonOptions));
        }
        else if (result.Healthy)
        {
            await output.WriteLineAsync("healthy");
        }
        else
        {
            foreach (var healthError in result.Errors)
            {
                await error.WriteLineAsync($"unhealthy: {healthError}");
            }
        }

        return result.Healthy ? 0 : 1;
    }

    private static LogLevel ParseLogLevel(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "warning" or "warn" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "critical" => LogLevel.Critical,
            _ => LogLevel.Information,
        };

    private static async Task<int> UnknownCommandAsync(string command, TextWriter error)
    {
        await error.WriteLineAsync($"error: unknown command '{command}'");
        await WriteUsageAsync(error);
        return 1;
    }

    private static ConfigLoadResult Load(CliOptions options)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                environment[key] = entry.Value?.ToString();
            }
        }

        var loader = new ConfigLoader();
        return loader.Load(new ConfigLoadOptions
        {
            ConfigPath = options.ConfigPath,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            Environment = environment,
        });
    }

    private static List<JobConfig> FilterJobs(RootConfig config, string? name) =>
        name is null
            ? config.Jobs
            : config.Jobs.Where(job => string.Equals(job.Name, name, StringComparison.Ordinal)).ToList();

    private static object BuildJobPlanModel(RootConfig config, JobConfig job)
    {
        var timezone = job.Timezone ?? config.Defaults.Timezone ?? "UTC";
        var destinations = job.Destinations.Select(destination =>
        {
            var retention = RetentionResolver.Resolve(
                config.Defaults.Retention,
                job.Retention,
                destination.Retention);
            return new
            {
                name = destination.Name,
                kind = destination.Kind,
                root = destination.Root,
                bucket = destination.Bucket,
                retention = DescribeRetention(retention),
            };
        }).ToList();

        return new
        {
            name = job.Name,
            enabled = job.IsEnabled,
            schedule = job.Schedule,
            intervalSeconds = job.IntervalSeconds,
            timezone,
            source = job.Source.Path,
            verify = job.Verify ?? "size",
            retention = DescribeRetention(RetentionResolver.Resolve(
                config.Defaults.Retention,
                job.Retention,
                destination: null)),
            destinations,
        };
    }

    private static void WriteJobPlan(TextWriter output, RootConfig config, JobConfig job)
    {
        var timezone = job.Timezone ?? config.Defaults.Timezone ?? "UTC";
        var schedule = job.Schedule ?? (job.IntervalSeconds is null
            ? "once"
            : $"every {job.IntervalSeconds}s");

        output.WriteLine($"job {job.Name}{(job.IsEnabled ? string.Empty : " (disabled)")}");
        output.WriteLine($"  schedule: {schedule}");
        output.WriteLine($"  timezone: {timezone}");
        output.WriteLine($"  source: {job.Source.Path}");
        output.WriteLine($"  verify: {job.Verify ?? "size"}");

        foreach (var destination in job.Destinations)
        {
            var retention = RetentionResolver.Resolve(
                config.Defaults.Retention,
                job.Retention,
                destination.Retention);
            // 不同 kind 的定位字段不一样：local 用 path，sftp 用 host+root，
            // s3 用 bucket+root，rclone 用 root。不要统一打印 root 造成空值。
            var location = destination.Kind.ToLowerInvariant() switch
            {
                "s3" => $"bucket={destination.Bucket} root={destination.Root}",
                "sftp" => $"host={destination.Host} root={destination.Root}",
                "local" => $"path={destination.Path}",
                _ => $"root={destination.Root}",
            };
            output.WriteLine($"  - {destination.Name} [{destination.Kind}] {location}");
            output.WriteLine($"    retention: {DescribeRetention(retention)}");
        }
    }

    private static string DescribeRetention(ResolvedRetention retention) =>
        retention.Enabled
            ? $"daily={retention.Daily}, weekly={retention.Weekly}, monthly={retention.Monthly}, max_delete={retention.MaxDelete}"
            : "disabled";

    private static string DescribeSource(ConfigLoadResult loaded) =>
        loaded.Source switch
        {
            ConfigSource.File => loaded.SourcePath ?? "<file>",
            ConfigSource.Inline => "<CRONBACKUP_CONFIG_YAML>",
            ConfigSource.Legacy => "<legacy environment>",
            _ => "<unknown>",
        };

    private static void WriteWarnings(
        ConfigLoadResult loaded,
        ConfigValidationResult validation,
        TextWriter error)
    {
        foreach (var warning in loaded.Warnings)
        {
            error.WriteLine($"warning: {warning}");
        }

        foreach (var warning in validation.Warnings)
        {
            error.WriteLine($"warning: {warning.Path}: {warning.Message}");
        }
    }

    private static async Task WriteUsageAsync(TextWriter writer)
    {
        await writer.WriteLineAsync("cron-backup <command> [options]");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("commands:");
        await writer.WriteLineAsync("  run       run jobs once and exit, or start the built-in scheduler");
        await writer.WriteLineAsync("  plan      show the effective configuration and planned actions");
        await writer.WriteLineAsync("  validate  validate configuration and environment placeholders");
        await writer.WriteLineAsync("  list      list jobs; add --remote to list remote objects");
        await writer.WriteLineAsync("  prune     apply retention; dry-run unless --apply is set");
        await writer.WriteLineAsync("  restore   download or extract an archive");
        await writer.WriteLineAsync("  health    check daemon status");
        await writer.WriteLineAsync("  version   print version information");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("options:");
        await writer.WriteLineAsync("  --config PATH        configuration file");
        await writer.WriteLineAsync("  --job NAME           select one job");
        await writer.WriteLineAsync("  --json               machine-readable output");
        await writer.WriteLineAsync("  --dry-run            force dry-run mode");
        await writer.WriteLineAsync("  --once               run once instead of starting the scheduler");
        await writer.WriteLineAsync("  --remote             list remote objects (list)");
        await writer.WriteLineAsync("  --tier TIER          daily|weekly|monthly (list/restore)");
        await writer.WriteLineAsync("  --destination NAME   destination (restore)");
        await writer.WriteLineAsync("  --key KEY            archive key (restore)");
        await writer.WriteLineAsync("  --latest             newest archive (restore)");
        await writer.WriteLineAsync("  --to DIR             restore destination directory");
        await writer.WriteLineAsync("  --extract            extract restored archive");
        await writer.WriteLineAsync("  --apply              actually delete (prune)");
        await writer.WriteLineAsync("  --strict             strict health check");
        await writer.WriteLineAsync("  --online             remote checks (not implemented)");
        await writer.WriteLineAsync("  --log-level LEVEL    trace|debug|info|warning|error");
        await writer.WriteLineAsync("  -h, --help           show this help");
    }
}
