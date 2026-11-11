using System.Globalization;

namespace CronBackup.Core.Configuration;

public static class LegacyJobFactory
{
    public static RootConfig Create(IReadOnlyDictionary<string, string?> environment)
    {
        var job = new JobConfig
        {
            Name = "default",
            Enabled = true,
            Schedule = Get(environment, "CRON_SCHEDULE"),
            IntervalSeconds = ParsePositiveInt(Get(environment, "INTERVAL_SECONDS"), "INTERVAL_SECONDS"),
            Verify = "size",
            Retention = new RetentionPolicy { Enabled = false },
            Source = new SourceConfig
            {
                Path = FirstNonEmpty(Get(environment, "BACKUP_SRC"), "/backup/data"),
            },
        };

        var remoteNames = FirstNonEmpty(Get(environment, "RCLONE_REMOTE_NAMES"), "backup")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (remoteNames.Length == 0)
        {
            throw new ConfigException("RCLONE_REMOTE_NAMES", "no remote names configured");
        }

        foreach (var remoteName in remoteNames)
        {
            job.Destinations.Add(CreateDestination(environment, remoteName));
        }

        var config = new RootConfig
        {
            Version = 1,
            Defaults = new DefaultsConfig
            {
                Timezone = Get(environment, "TZ"),
                LogTarget = Get(environment, "LOG_TARGET"),
                DryRun = ParseBool(Get(environment, "DRY_RUN"), "DRY_RUN"),
            },
            Jobs = [job],
        };

        var notifyUrl = Get(environment, "NOTIFY_URL");
        if (!string.IsNullOrWhiteSpace(notifyUrl))
        {
            config.Defaults.Notify =
            [
                new NotifyConfig
                {
                    Kind = "webhook",
                    Url = notifyUrl,
                },
            ];
        }

        return config;
    }

    private static DestinationConfig CreateDestination(
        IReadOnlyDictionary<string, string?> environment,
        string remoteName)
    {
        var type = Get(environment, $"RCLONE_{remoteName}_TYPE");
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ConfigException(
                $"RCLONE_{remoteName}_TYPE",
                $"remote '{remoteName}' has no type");
        }

        var root = NormalizeRoot(
            FirstNonEmpty(
                Get(environment, $"RCLONE_{remoteName}_PATH"),
                Get(environment, "RCLONE_PATH"),
                "/backups"));

        var destination = new DestinationConfig
        {
            Name = remoteName,
            Root = root,
        };

        switch (type.ToLowerInvariant())
        {
            case "s3":
                destination.Kind = "s3";
                destination.Provider = FirstNonEmpty(
                    Get(environment, $"RCLONE_{remoteName}_PROVIDER"),
                    "AWS");
                destination.Endpoint = Get(environment, $"RCLONE_{remoteName}_ENDPOINT");
                destination.Region = FirstNonEmpty(
                    Get(environment, $"RCLONE_{remoteName}_REGION"),
                    "us-east-1");
                destination.Bucket = FirstNonEmpty(
                    Get(environment, $"RCLONE_{remoteName}_BUCKET"),
                    "backups");
                destination.AccessKeyId = Get(environment, $"RCLONE_{remoteName}_ACCESS_KEY_ID");
                destination.SecretAccessKey = Get(environment, $"RCLONE_{remoteName}_SECRET_ACCESS_KEY");
                destination.EnvAuth = ParseBool(
                    Get(environment, $"RCLONE_{remoteName}_ENV_AUTH"),
                    $"RCLONE_{remoteName}_ENV_AUTH") ?? false;
                destination.Acl = FirstNonEmpty(
                    Get(environment, $"RCLONE_{remoteName}_ACL"),
                    "private");
                destination.NoCheckBucket = ParseBool(
                    Get(environment, $"RCLONE_{remoteName}_NO_CHECK_BUCKET"),
                    $"RCLONE_{remoteName}_NO_CHECK_BUCKET") ?? true;
                break;

            case "sftp":
                destination.Kind = "sftp";
                destination.Host = Get(environment, $"RCLONE_{remoteName}_HOST");
                destination.User = Get(environment, $"RCLONE_{remoteName}_USER");
                destination.Password = Get(environment, $"RCLONE_{remoteName}_PASS");
                destination.KeyFile = Get(environment, $"RCLONE_{remoteName}_KEY_FILE");
                destination.Port = ParsePositiveInt(
                    Get(environment, $"RCLONE_{remoteName}_PORT"),
                    $"RCLONE_{remoteName}_PORT") ?? 22;
                break;

            case "drive":
                destination.Kind = "rclone";
                destination.Type = "drive";
                destination.Options = new Dictionary<string, string>(StringComparer.Ordinal);
                AddOption(environment, destination.Options, $"RCLONE_{remoteName}_CLIENT_ID", "client_id");
                AddOption(environment, destination.Options, $"RCLONE_{remoteName}_CLIENT_SECRET", "client_secret");
                AddOption(environment, destination.Options, $"RCLONE_{remoteName}_TOKEN", "token");
                break;

            default:
                throw new ConfigException(
                    $"RCLONE_{remoteName}_TYPE",
                    $"unsupported rclone type '{type}' for remote '{remoteName}'");
        }

        return destination;
    }

    private static void AddOption(
        IReadOnlyDictionary<string, string?> environment,
        IDictionary<string, string> options,
        string environmentKey,
        string optionKey)
    {
        var value = Get(environment, environmentKey);
        if (!string.IsNullOrEmpty(value))
        {
            options[optionKey] = value;
        }
    }

    private static string NormalizeRoot(string value)
    {
        var trimmed = value.Trim();
        if (trimmed is "" or "/")
        {
            return string.Empty;
        }

        return trimmed.Trim('/');
    }

    private static string? Get(IReadOnlyDictionary<string, string?> environment, string key) =>
        environment.TryGetValue(key, out var value) ? value : null;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static bool? ParseBool(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new ConfigException(key, $"invalid boolean value '{value}'"),
        };
    }

    private static int? ParsePositiveInt(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new ConfigException(key, $"expected a positive integer, got '{value}'");
        }

        return parsed;
    }
}
