using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CronBackup.Core.Configuration;

public enum ConfigSource
{
    File,
    Inline,
    Legacy,
}

public sealed record ConfigLoadResult(
    RootConfig Config,
    ConfigSource Source,
    string? SourcePath,
    IReadOnlyList<string> Warnings);

public sealed class ConfigLoadOptions
{
    public string? ConfigPath { get; init; }

    public string? WorkingDirectory { get; init; }

    public required IReadOnlyDictionary<string, string?> Environment { get; init; }

    public bool AllowLegacy { get; init; } = true;
}

public sealed class ConfigLoader
{
    private static readonly string[] LegacyKeys =
    [
        "BACKUP_SRC",
        "RCLONE_REMOTE_NAMES",
        "RCLONE_PATH",
        "CRON_SCHEDULE",
        "INTERVAL_SECONDS",
    ];

    public ConfigLoadResult Load(ConfigLoadOptions options)
    {
        var environment = options.Environment;
        var workingDirectory = options.WorkingDirectory ?? Directory.GetCurrentDirectory();
        var warnings = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.ConfigPath))
        {
            var explicitPath = Path.GetFullPath(options.ConfigPath, workingDirectory);
            if (!File.Exists(explicitPath))
            {
                throw new ConfigException("--config", $"configuration file not found: {explicitPath}");
            }

            return LoadFile(explicitPath, environment, warnings);
        }

        var environmentPath = GetEnvironmentValue(environment, "CRONBACKUP_CONFIG");
        var inlineYaml = GetEnvironmentValue(environment, "CRONBACKUP_CONFIG_YAML");

        if (!string.IsNullOrWhiteSpace(environmentPath) && !string.IsNullOrWhiteSpace(inlineYaml))
        {
            throw new ConfigException(
                "CRONBACKUP_CONFIG",
                "CRONBACKUP_CONFIG and CRONBACKUP_CONFIG_YAML cannot be used together");
        }

        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            var path = Path.GetFullPath(environmentPath, workingDirectory);
            if (!File.Exists(path))
            {
                throw new ConfigException("CRONBACKUP_CONFIG", $"configuration file not found: {path}");
            }

            return LoadFile(path, environment, warnings);
        }

        if (!string.IsNullOrWhiteSpace(inlineYaml))
        {
            return LoadText(inlineYaml, ConfigSource.Inline, null, environment, warnings);
        }

        foreach (var candidate in GetDefaultPaths(workingDirectory))
        {
            if (File.Exists(candidate))
            {
                return LoadFile(candidate, environment, warnings);
            }
        }

        if (!options.AllowLegacy)
        {
            throw new ConfigException("config", "no configuration file found");
        }

        foreach (var key in LegacyKeys)
        {
            if (!string.IsNullOrWhiteSpace(GetEnvironmentValue(environment, key)))
            {
                warnings.Add($"found legacy environment variable {key}; no config.yaml, using implicit job");
            }
        }

        var legacyConfig = LegacyJobFactory.Create(environment);
        return new ConfigLoadResult(legacyConfig, ConfigSource.Legacy, null, warnings);
    }

    private static ConfigLoadResult LoadFile(
        string path,
        IReadOnlyDictionary<string, string?> environment,
        List<string> warnings)
    {
        var yaml = File.ReadAllText(path);
        var result = LoadText(yaml, ConfigSource.File, path, environment, warnings);

        foreach (var key in LegacyKeys)
        {
            if (!string.IsNullOrWhiteSpace(GetEnvironmentValue(environment, key)))
            {
                warnings.Add($"config.yaml is present; ignoring legacy environment variable {key}");
            }
        }

        return result;
    }

    private static ConfigLoadResult LoadText(
        string yaml,
        ConfigSource source,
        string? sourcePath,
        IReadOnlyDictionary<string, string?> environment,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new ConfigException("config", "configuration is empty");
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();

        RootConfig? config;
        try
        {
            config = deserializer.Deserialize<RootConfig>(yaml);
        }
        catch (YamlException exception)
        {
            throw new ConfigException(sourcePath ?? "config", $"invalid YAML: {exception.Message}", exception);
        }

        if (config is null)
        {
            throw new ConfigException(sourcePath ?? "config", "configuration is empty");
        }

        config.Defaults ??= new DefaultsConfig();
        config.Jobs ??= new List<JobConfig>();
        foreach (var job in config.Jobs)
        {
            job.Source ??= new SourceConfig();
            job.Destinations ??= new List<DestinationConfig>();
        }

        ConfigInterpolator.Interpolate(config, new EnvInterpolator(environment));

        return new ConfigLoadResult(config, source, sourcePath, warnings);
    }

    private static IEnumerable<string> GetDefaultPaths(string workingDirectory)
    {
        yield return "/config/config.yaml";
        yield return Path.Combine(workingDirectory, "config.yaml");
    }

    internal static string? GetEnvironmentValue(IReadOnlyDictionary<string, string?> environment, string key) =>
        environment.TryGetValue(key, out var value) ? value : null;
}
