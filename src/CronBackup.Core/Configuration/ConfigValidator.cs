using System.Text.RegularExpressions;
using Cronos;

namespace CronBackup.Core.Configuration;

public sealed record ValidationIssue(string Path, string Message);

public sealed class ConfigValidationResult
{
    public List<ValidationIssue> Errors { get; } = new();

    public List<ValidationIssue> Warnings { get; } = new();

    public bool IsValid => Errors.Count == 0;
}

public sealed partial class ConfigValidator
{
    private static readonly HashSet<string> DestinationKinds =
        new(StringComparer.OrdinalIgnoreCase) { "s3", "sftp", "local", "rclone" };

    private static readonly HashSet<string> VerifyModes =
        new(StringComparer.OrdinalIgnoreCase) { "none", "size", "checksum" };

    private static readonly HashSet<string> NotifyKinds =
        new(StringComparer.OrdinalIgnoreCase) { "webhook", "ntfy", "healthchecks" };

    public ConfigValidationResult Validate(RootConfig config)
    {
        var result = new ConfigValidationResult();

        if (config.Version != 1)
        {
            result.Errors.Add(new ValidationIssue("version", "only version 1 is supported"));
        }

        ValidateDefaults(config.Defaults, result);

        if (config.Jobs.Count == 0)
        {
            result.Errors.Add(new ValidationIssue("jobs", "at least one job is required"));
        }

        var duplicateNames = config.Jobs
            .Where(job => !string.IsNullOrWhiteSpace(job.Name))
            .GroupBy(job => job.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);

        foreach (var name in duplicateNames)
        {
            result.Errors.Add(new ValidationIssue("jobs", $"duplicate job name '{name}'"));
        }

        for (var index = 0; index < config.Jobs.Count; index++)
        {
            ValidateJob(config.Jobs[index], $"jobs[{index}]", result);
        }

        return result;
    }

    public void ThrowIfInvalid(RootConfig config)
    {
        var result = Validate(config);
        if (result.Errors.Count > 0)
        {
            throw new ConfigException(result.Errors[0].Path, result.Errors[0].Message);
        }
    }

    private static void ValidateDefaults(DefaultsConfig defaults, ConfigValidationResult result)
    {
        if (defaults.Timezone is not null)
        {
            ValidateTimezone(defaults.Timezone, "defaults.timezone", result);
        }

        if (defaults.LogTarget is not null &&
            !string.Equals(defaults.LogTarget, "console", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(defaults.LogTarget, "file", StringComparison.OrdinalIgnoreCase))
        {
            result.Errors.Add(new ValidationIssue(
                "defaults.log_target",
                "expected 'console' or 'file'"));
        }

        ValidateRetention(defaults.Retention, "defaults.retention", result);
        ValidateArchive(defaults.Archive, "defaults.archive", result);

        if (defaults.Notify is not null)
        {
            for (var index = 0; index < defaults.Notify.Count; index++)
            {
                ValidateNotify(defaults.Notify[index], $"defaults.notify[{index}]", result);
            }
        }
    }

    private static void ValidateJob(JobConfig job, string path, ConfigValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(job.Name))
        {
            result.Errors.Add(new ValidationIssue($"{path}.name", "job name is required"));
        }
        else if (!JobNameRegex().IsMatch(job.Name))
        {
            result.Errors.Add(new ValidationIssue(
                $"{path}.name",
                "job name must start with a letter or digit and contain only letters, digits, '.', '_' or '-'"));
        }

        if (job.Timezone is not null)
        {
            ValidateTimezone(job.Timezone, $"{path}.timezone", result);
        }

        if (job.Schedule is not null && job.IntervalSeconds is not null)
        {
            result.Errors.Add(new ValidationIssue(
                path,
                "schedule and interval_seconds cannot be used together"));
        }

        if (job.Schedule is not null && !IsValidCron(job.Schedule))
        {
            result.Errors.Add(new ValidationIssue($"{path}.schedule", $"invalid cron expression '{job.Schedule}'"));
        }

        if (job.IntervalSeconds is <= 0)
        {
            result.Errors.Add(new ValidationIssue($"{path}.interval_seconds", "must be a positive integer"));
        }

        if (job.Schedule is null && job.IntervalSeconds is null)
        {
            result.Warnings.Add(new ValidationIssue(
                path,
                "no schedule configured; the job will run once and exit"));
        }

        if (job.Verify is not null && !VerifyModes.Contains(job.Verify))
        {
            result.Errors.Add(new ValidationIssue(
                $"{path}.verify",
                "expected 'none', 'size' or 'checksum'"));
        }

        ValidateRetention(job.Retention, $"{path}.retention", result);
        ValidateArchive(job.Archive, $"{path}.archive", result);
        ValidateHooks(job.Hooks, $"{path}.hooks", result);

        if (job.Notify is not null)
        {
            for (var index = 0; index < job.Notify.Count; index++)
            {
                ValidateNotify(job.Notify[index], $"{path}.notify[{index}]", result);
            }
        }

        // disabled job 允许暂时没有 source/destination，但仍校验 name、schedule、
        // retention 等结构，避免在重新启用时才发现配置坏了。
        if (!job.IsEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(job.Source.Path))
        {
            result.Errors.Add(new ValidationIssue($"{path}.source.path", "source path is required"));
        }

        if (job.Destinations.Count == 0)
        {
            result.Errors.Add(new ValidationIssue($"{path}.destinations", "at least one destination is required"));
        }

        var duplicateNames = job.Destinations
            .Where(destination => !string.IsNullOrWhiteSpace(destination.Name))
            .GroupBy(destination => destination.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);

        foreach (var name in duplicateNames)
        {
            result.Errors.Add(new ValidationIssue(
                $"{path}.destinations",
                $"duplicate destination name '{name}'"));
        }

        for (var index = 0; index < job.Destinations.Count; index++)
        {
            ValidateDestination(job.Destinations[index], $"{path}.destinations[{index}]", result);
        }
    }

    private static void ValidateDestination(
        DestinationConfig destination,
        string path,
        ConfigValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(destination.Name))
        {
            result.Errors.Add(new ValidationIssue($"{path}.name", "destination name is required"));
        }

        if (string.IsNullOrWhiteSpace(destination.Kind) || !DestinationKinds.Contains(destination.Kind))
        {
            result.Errors.Add(new ValidationIssue(
                $"{path}.kind",
                "expected 's3', 'sftp', 'local' or 'rclone'"));
            return;
        }

        ValidateRetention(destination.Retention, $"{path}.retention", result);

        switch (destination.Kind.ToLowerInvariant())
        {
            case "s3":
                if (string.IsNullOrWhiteSpace(destination.Bucket))
                {
                    result.Errors.Add(new ValidationIssue($"{path}.bucket", "bucket is required for s3"));
                }

                if (string.IsNullOrWhiteSpace(destination.Provider))
                {
                    result.Errors.Add(new ValidationIssue($"{path}.provider", "provider is required for s3"));
                }

                if (destination.EnvAuth != true &&
                    (string.IsNullOrWhiteSpace(destination.AccessKeyId) ||
                     string.IsNullOrWhiteSpace(destination.SecretAccessKey)))
                {
                    result.Warnings.Add(new ValidationIssue(
                        path,
                        "s3 credentials are not set; use env_auth or access_key_id/secret_access_key"));
                }

                break;

            case "sftp":
                if (string.IsNullOrWhiteSpace(destination.Host))
                {
                    result.Errors.Add(new ValidationIssue($"{path}.host", "host is required for sftp"));
                }

                if (string.IsNullOrWhiteSpace(destination.User))
                {
                    result.Errors.Add(new ValidationIssue($"{path}.user", "user is required for sftp"));
                }

                if (string.IsNullOrWhiteSpace(destination.Password) &&
                    string.IsNullOrWhiteSpace(destination.KeyFile))
                {
                    result.Errors.Add(new ValidationIssue(
                        path,
                        "sftp requires password or key_file"));
                }

                if (destination.Port is <= 0 or > 65535)
                {
                    result.Errors.Add(new ValidationIssue($"{path}.port", "port must be between 1 and 65535"));
                }

                if (string.IsNullOrWhiteSpace(destination.KnownHostsFile))
                {
                    result.Warnings.Add(new ValidationIssue(
                        path,
                        "sftp has no known_hosts_file; rclone will not validate the host key"));
                }

                break;

            case "local":
                if (string.IsNullOrWhiteSpace(destination.Path))
                {
                    result.Errors.Add(new ValidationIssue($"{path}.path", "path is required for local"));
                }

                break;

            case "rclone":
                if (string.IsNullOrWhiteSpace(destination.Type))
                {
                    result.Errors.Add(new ValidationIssue($"{path}.type", "type is required for rclone"));
                }

                if (destination.Obscure is not null)
                {
                    foreach (var option in destination.Obscure)
                    {
                        if (destination.Options is null || !destination.Options.ContainsKey(option))
                        {
                            result.Warnings.Add(new ValidationIssue(
                                $"{path}.obscure",
                                $"obscure option '{option}' is not present in options"));
                        }
                    }
                }

                break;
        }

        if (destination.Root is not null && destination.Root.Contains('\\'))
        {
            result.Errors.Add(new ValidationIssue(
                $"{path}.root",
                "root must use '/' separators"));
        }
    }

    private static void ValidateRetention(
        RetentionPolicy? retention,
        string path,
        ConfigValidationResult result)
    {
        if (retention is null)
        {
            return;
        }

        ValidateTier(retention.Daily, $"{path}.daily", result);
        ValidateTier(retention.Weekly, $"{path}.weekly", result);
        ValidateTier(retention.Monthly, $"{path}.monthly", result);

        if (retention.MaxDelete is <= 0)
        {
            result.Errors.Add(new ValidationIssue($"{path}.max_delete", "must be a positive integer"));
        }

        if (retention.Enabled == true &&
            (retention.Daily ?? 0) == 0 &&
            (retention.Weekly ?? 0) == 0 &&
            (retention.Monthly ?? 0) == 0)
        {
            result.Warnings.Add(new ValidationIssue(
                path,
                "retention is enabled but all tiers are zero"));
        }
    }

    private static void ValidateTier(int? value, string path, ConfigValidationResult result)
    {
        if (value is < 0)
        {
            result.Errors.Add(new ValidationIssue(path, "must be zero or a positive integer"));
        }
    }

    private static void ValidateArchive(
        ArchiveOptions? archive,
        string path,
        ConfigValidationResult result)
    {
        if (archive is null)
        {
            return;
        }

        if (archive.Format is not null &&
            !string.Equals(archive.Format, "tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            result.Errors.Add(new ValidationIssue($"{path}.format", "only 'tar.gz' is supported"));
        }

        if (archive.TempDir is not null && string.IsNullOrWhiteSpace(archive.TempDir))
        {
            result.Errors.Add(new ValidationIssue($"{path}.temp_dir", "temp_dir cannot be empty"));
        }
    }

    private static void ValidateHooks(
        HooksConfig? hooks,
        string path,
        ConfigValidationResult result)
    {
        if (hooks is null)
        {
            return;
        }

        if (hooks.TimeoutSeconds is <= 0)
        {
            result.Errors.Add(new ValidationIssue($"{path}.timeout_seconds", "must be a positive integer"));
        }

        ValidateHookList(hooks.Pre, $"{path}.pre", result);
        ValidateHookList(hooks.Post, $"{path}.post", result);
    }

    private static void ValidateHookList(
        List<HookDefinition>? hooks,
        string path,
        ConfigValidationResult result)
    {
        if (hooks is null)
        {
            return;
        }

        for (var index = 0; index < hooks.Count; index++)
        {
            var hook = hooks[index];
            var hookPath = $"{path}[{index}]";
            if (string.IsNullOrWhiteSpace(hook.Run))
            {
                result.Errors.Add(new ValidationIssue($"{hookPath}.run", "run is required"));
            }

            if (hook.TimeoutSeconds is <= 0)
            {
                result.Errors.Add(new ValidationIssue($"{hookPath}.timeout_seconds", "must be a positive integer"));
            }

            if (hook.Env is not null && hook.Env.Keys.Any(string.IsNullOrWhiteSpace))
            {
                result.Errors.Add(new ValidationIssue($"{hookPath}.env", "environment variable name cannot be empty"));
            }
        }
    }

    private static void ValidateNotify(
        NotifyConfig notify,
        string path,
        ConfigValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(notify.Kind) || !NotifyKinds.Contains(notify.Kind))
        {
            result.Errors.Add(new ValidationIssue(
                $"{path}.kind",
                "expected 'webhook', 'ntfy' or 'healthchecks'"));
            return;
        }

        if (string.IsNullOrWhiteSpace(notify.Url) ||
            !Uri.TryCreate(notify.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            result.Errors.Add(new ValidationIssue($"{path}.url", "an absolute http(s) URL is required"));
        }
    }

    private static void ValidateTimezone(
        string timezone,
        string path,
        ConfigValidationResult result)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            result.Errors.Add(new ValidationIssue(path, $"unknown timezone '{timezone}'"));
        }
        catch (InvalidTimeZoneException)
        {
            result.Errors.Add(new ValidationIssue(path, $"invalid timezone '{timezone}'"));
        }
    }

    private static bool IsValidCron(string schedule) =>
        CronExpression.TryParse(schedule, CronFormat.Standard, out _) ||
        CronExpression.TryParse(schedule, CronFormat.IncludeSeconds, out _);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex JobNameRegex();
}
