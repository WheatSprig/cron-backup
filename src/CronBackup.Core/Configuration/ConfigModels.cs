namespace CronBackup.Core.Configuration;

public sealed class RootConfig
{
    public int Version { get; set; } = 1;

    public DefaultsConfig Defaults { get; set; } = new();

    public List<JobConfig> Jobs { get; set; } = new();

    public JobConfig? FindJob(string name) =>
        Jobs.FirstOrDefault(job => string.Equals(job.Name, name, StringComparison.Ordinal));
}

public sealed class DefaultsConfig
{
    public string? Timezone { get; set; }

    public string? LogTarget { get; set; }

    public bool? DryRun { get; set; }

    public int? MaxParallel { get; set; }

    public RetentionPolicy? Retention { get; set; }

    public ArchiveOptions? Archive { get; set; }

    public List<NotifyConfig>? Notify { get; set; }
}

public sealed class JobConfig
{
    public string Name { get; set; } = string.Empty;

    public bool? Enabled { get; set; }

    public string? Schedule { get; set; }

    public int? IntervalSeconds { get; set; }

    public string? Timezone { get; set; }

    public SourceConfig Source { get; set; } = new();

    public List<DestinationConfig> Destinations { get; set; } = new();

    public RetentionPolicy? Retention { get; set; }

    public HooksConfig? Hooks { get; set; }

    public List<NotifyConfig>? Notify { get; set; }

    public string? Verify { get; set; }

    public ArchiveOptions? Archive { get; set; }

    public bool IsEnabled => Enabled ?? true;
}

public sealed class SourceConfig
{
    public string Path { get; set; } = string.Empty;
}

public sealed class DestinationConfig
{
    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string? Provider { get; set; }

    public string? Endpoint { get; set; }

    public string? Region { get; set; }

    public string? Bucket { get; set; }

    public string? Root { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    public bool? EnvAuth { get; set; }

    public string? Acl { get; set; }

    public bool? NoCheckBucket { get; set; }

    public string? Host { get; set; }

    public int? Port { get; set; }

    public string? User { get; set; }

    public string? Password { get; set; }

    public string? KeyFile { get; set; }

    public string? KnownHostsFile { get; set; }

    public string? Path { get; set; }

    public string? Type { get; set; }

    public Dictionary<string, string>? Options { get; set; }

    public List<string>? Obscure { get; set; }

    public RetentionPolicy? Retention { get; set; }
}

public sealed class RetentionPolicy
{
    public bool? Enabled { get; set; }

    public int? Daily { get; set; }

    public int? Weekly { get; set; }

    public int? Monthly { get; set; }

    public int? MaxDelete { get; set; }

    public RetentionPolicy Clone() => new()
    {
        Enabled = Enabled,
        Daily = Daily,
        Weekly = Weekly,
        Monthly = Monthly,
        MaxDelete = MaxDelete,
    };
}

public sealed class ArchiveOptions
{
    public string? Format { get; set; }

    public string? TempDir { get; set; }

    public int? TimeoutSeconds { get; set; }
}

public sealed class HooksConfig
{
    public int? TimeoutSeconds { get; set; }

    public List<HookDefinition>? Pre { get; set; }

    public List<HookDefinition>? Post { get; set; }
}

public sealed class HookDefinition
{
    public string Run { get; set; } = string.Empty;

    public List<string>? Args { get; set; }

    public int? TimeoutSeconds { get; set; }

    public string? Cwd { get; set; }

    public Dictionary<string, string>? Env { get; set; }
}

public sealed class NotifyConfig
{
    public string Kind { get; set; } = string.Empty;

    public string? Url { get; set; }

    public string? Topic { get; set; }
}
