using CronBackup.Core.Configuration;
using CronBackup.Core.Tests.TestHelpers;

namespace CronBackup.Core.Tests.Configuration;

public sealed class ConfigLoaderTests
{
    private const string MinimalConfig = """
        version: 1
        jobs:
          - name: photos
            schedule: "0 2 * * *"
            source:
              path: /backup/data
            destinations:
              - name: local
                kind: local
                path: /backups
        """;

    [Fact]
    public void Load_ShouldReadInlineYaml()
    {
        var environment = new Dictionary<string, string?>
        {
            ["CRONBACKUP_CONFIG_YAML"] = MinimalConfig,
        };

        var result = new ConfigLoader().Load(new ConfigLoadOptions
        {
            Environment = environment,
            WorkingDirectory = Path.GetTempPath(),
        });

        Assert.Equal(ConfigSource.Inline, result.Source);
        Assert.Single(result.Config.Jobs);
        Assert.Equal("photos", result.Config.Jobs[0].Name);
    }

    [Fact]
    public void Load_ShouldInterpolateEnvironmentVariables()
    {
        var yaml = MinimalConfig.Replace(
            "path: /backups",
            "path: ${BACKUP_ROOT}");
        var environment = new Dictionary<string, string?>
        {
            ["CRONBACKUP_CONFIG_YAML"] = yaml,
            ["BACKUP_ROOT"] = "/mnt/backups",
        };

        var result = new ConfigLoader().Load(new ConfigLoadOptions
        {
            Environment = environment,
            WorkingDirectory = Path.GetTempPath(),
        });

        Assert.Equal("/mnt/backups", result.Config.Jobs[0].Destinations[0].Path);
    }

    [Fact]
    public void Load_ShouldReadExplicitFile()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.yaml");
        File.WriteAllText(path, MinimalConfig);

        var result = new ConfigLoader().Load(new ConfigLoadOptions
        {
            ConfigPath = path,
            Environment = new Dictionary<string, string?>(),
            WorkingDirectory = temp.Path,
        });

        Assert.Equal(ConfigSource.File, result.Source);
        Assert.Equal(path, result.SourcePath);
    }

    [Fact]
    public void Load_ShouldThrow_WhenExplicitFileIsMissing()
    {
        using var temp = new TempDirectory();

        var exception = Assert.Throws<ConfigException>(() => new ConfigLoader().Load(new ConfigLoadOptions
        {
            ConfigPath = temp.File("missing.yaml"),
            Environment = new Dictionary<string, string?>(),
            WorkingDirectory = temp.Path,
        }));

        Assert.Contains("not found", exception.Message);
    }

    [Fact]
    public void Load_ShouldThrow_WhenYamlHasUnknownField()
    {
        var yaml = MinimalConfig + "\nunknown_field: true\n";
        var environment = new Dictionary<string, string?>
        {
            ["CRONBACKUP_CONFIG_YAML"] = yaml,
        };

        Assert.Throws<ConfigException>(() => new ConfigLoader().Load(new ConfigLoadOptions
        {
            Environment = environment,
            WorkingDirectory = Path.GetTempPath(),
        }));
    }

    [Fact]
    public void Load_ShouldThrow_WhenYamlHasDuplicateKey()
    {
        var environment = new Dictionary<string, string?>
        {
            ["CRONBACKUP_CONFIG_YAML"] = "version: 1\nversion: 1\njobs: []\n",
        };

        Assert.Throws<ConfigException>(() => new ConfigLoader().Load(new ConfigLoadOptions
        {
            Environment = environment,
            WorkingDirectory = Path.GetTempPath(),
        }));
    }

    [Fact]
    public void Load_ShouldThrow_WhenPathAndInlineAreBothSet()
    {
        var environment = new Dictionary<string, string?>
        {
            ["CRONBACKUP_CONFIG"] = "config.yaml",
            ["CRONBACKUP_CONFIG_YAML"] = MinimalConfig,
        };

        var exception = Assert.Throws<ConfigException>(() => new ConfigLoader().Load(new ConfigLoadOptions
        {
            Environment = environment,
            WorkingDirectory = Path.GetTempPath(),
        }));

        Assert.Contains("cannot be used together", exception.Message);
    }

    [Fact]
    public void Load_ShouldCreateLegacyJob_WhenNoConfigExists()
    {
        using var temp = new TempDirectory();
        var environment = new Dictionary<string, string?>
        {
            ["BACKUP_SRC"] = "/data",
            ["RCLONE_REMOTE_NAMES"] = "backup",
            ["RCLONE_backup_TYPE"] = "sftp",
            ["RCLONE_backup_HOST"] = "nas.local",
            ["RCLONE_backup_USER"] = "backup",
            ["RCLONE_backup_PASS"] = "secret",
            ["RCLONE_PATH"] = "/daily",
        };

        var result = new ConfigLoader().Load(new ConfigLoadOptions
        {
            Environment = environment,
            WorkingDirectory = temp.Path,
        });

        Assert.Equal(ConfigSource.Legacy, result.Source);
        Assert.Single(result.Config.Jobs);
        Assert.Equal("/data", result.Config.Jobs[0].Source.Path);
        Assert.Equal("sftp", result.Config.Jobs[0].Destinations[0].Kind);
        Assert.Equal("daily", result.Config.Jobs[0].Destinations[0].Root);
    }

    [Fact]
    public void Load_ShouldWarnAboutIgnoredLegacyVariables_WhenFileExists()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.yaml");
        File.WriteAllText(path, MinimalConfig);
        var environment = new Dictionary<string, string?>
        {
            ["BACKUP_SRC"] = "/ignored",
        };

        var result = new ConfigLoader().Load(new ConfigLoadOptions
        {
            ConfigPath = path,
            Environment = environment,
            WorkingDirectory = temp.Path,
        });

        Assert.Contains(result.Warnings, warning => warning.Contains("BACKUP_SRC", StringComparison.Ordinal));
    }
}
