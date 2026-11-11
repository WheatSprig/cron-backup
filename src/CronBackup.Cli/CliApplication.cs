using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CronBackup.Core.Configuration;
using CronBackup.Core.Execution;
using CronBackup.Core.Rclone;
using CronBackup.Core.Retention;
using CronBackup.Core.Storage;
using Microsoft.Extensions.Logging;

namespace CronBackup.Cli;

public static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
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
                "prune" => await NotImplementedAsync("prune", standardOutput, standardError),
                "restore" => await NotImplementedAsync("restore", standardOutput, standardError),
                "health" => await NotImplementedAsync("health", standardOutput, standardError),
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

        if (!options.Once && jobs.Any(job => job.Schedule is not null || job.IntervalSeconds is not null))
        {
            await error.WriteLineAsync("error: daemon scheduling is not implemented in this build; use --once");
            return 1;
        }

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(ParseLogLevel(options.LogLevel));
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
        var runner = new JobRunner(
            new TarArchiveService(processRunner),
            storageFactory,
            new FileJobLockFactory(),
            new HookRunner(processRunner, loggerFactory.CreateLogger<HookRunner>()),
            new HttpNotifier(httpClient),
            loggerFactory.CreateLogger<JobRunner>());

        var results = new List<JobRunResult>();
        foreach (var job in jobs)
        {
            results.Add(await runner.RunAsync(
                loaded.Config,
                job,
                new JobRunOptions(options.DryRun),
                cancellationToken));
        }

        if (options.Json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                dryRun = options.DryRun || loaded.Config.Defaults.DryRun == true,
                jobs = results.Select(result => new
                {
                    name = result.JobName,
                    status = result.Status.ToString().ToLowerInvariant(),
                    archive = result.ArchiveName,
                    error = result.Error,
                    destinations = result.Destinations.Select(destination => new
                    {
                        name = destination.Name,
                        success = destination.Success,
                        uploaded = destination.Uploaded,
                        promoted = destination.Promoted,
                        deleted = destination.Deleted,
                        error = destination.Error,
                    }),
                }),
            }, JsonOptions));
        }
        else
        {
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

        if (results.Any(result => result.Status == JobRunStatus.Locked))
        {
            return 4;
        }

        return results.All(result => result.Status is JobRunStatus.Success or JobRunStatus.Skipped) ? 0 : 3;
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

    private static async Task<int> NotImplementedAsync(
        string command,
        TextWriter output,
        TextWriter error)
    {
        await error.WriteLineAsync($"error: '{command}' is not implemented in this skeleton build");
        await output.WriteLineAsync("implemented commands: help, version, validate, plan, list");
        return 3;
    }

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
            var location = destination.Kind.Equals("s3", StringComparison.OrdinalIgnoreCase)
                ? $"bucket={destination.Bucket} root={destination.Root}"
                : $"root={destination.Root}";
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
        await writer.WriteLineAsync("  run       run jobs (not implemented in this build)");
        await writer.WriteLineAsync("  plan      show the effective configuration and planned actions");
        await writer.WriteLineAsync("  validate  validate configuration and environment placeholders");
        await writer.WriteLineAsync("  list      list jobs and destinations");
        await writer.WriteLineAsync("  prune     apply retention (not implemented in this build)");
        await writer.WriteLineAsync("  restore   restore an archive (not implemented in this build)");
        await writer.WriteLineAsync("  health    check daemon status (not implemented in this build)");
        await writer.WriteLineAsync("  version   print version information");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("options:");
        await writer.WriteLineAsync("  --config PATH   configuration file");
        await writer.WriteLineAsync("  --job NAME      select one job");
        await writer.WriteLineAsync("  --json          machine-readable output");
        await writer.WriteLineAsync("  --dry-run       force dry-run mode");
        await writer.WriteLineAsync("  --online        perform remote checks (not implemented)");
        await writer.WriteLineAsync("  -h, --help      show this help");
    }
}
