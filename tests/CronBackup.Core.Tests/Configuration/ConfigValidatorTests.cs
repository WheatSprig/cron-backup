using CronBackup.Core.Configuration;

namespace CronBackup.Core.Tests.Configuration;

public sealed class ConfigValidatorTests
{
    [Fact]
    public void Validate_ShouldAcceptMinimalLocalConfig()
    {
        var result = new ConfigValidator().Validate(CreateValidConfig());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_ShouldRejectUnknownDestinationKind()
    {
        var config = CreateValidConfig();
        config.Jobs[0].Destinations[0].Kind = "ftp";

        var result = new ConfigValidator().Validate(config);

        Assert.Contains(result.Errors, issue => issue.Path.EndsWith(".kind", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldRejectMissingSource()
    {
        var config = CreateValidConfig();
        config.Jobs[0].Source.Path = "";

        var result = new ConfigValidator().Validate(config);

        Assert.Contains(result.Errors, issue => issue.Path.EndsWith(".source.path", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldRejectInvalidCron()
    {
        var config = CreateValidConfig();
        config.Jobs[0].Schedule = "not a cron";

        var result = new ConfigValidator().Validate(config);

        Assert.Contains(result.Errors, issue => issue.Path.EndsWith(".schedule", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldAcceptSixFieldCron()
    {
        var config = CreateValidConfig();
        config.Jobs[0].Schedule = "0 0 2 * * *";

        var result = new ConfigValidator().Validate(config);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_ShouldRejectDuplicateJobNames()
    {
        var config = CreateValidConfig();
        config.Jobs.Add(CreateValidConfig().Jobs[0]);

        var result = new ConfigValidator().Validate(config);

        Assert.Contains(result.Errors, issue => issue.Message.Contains("duplicate job name", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldRejectNegativeRetention()
    {
        var config = CreateValidConfig();
        config.Jobs[0].Retention = new RetentionPolicy { Daily = -1 };

        var result = new ConfigValidator().Validate(config);

        Assert.Contains(result.Errors, issue => issue.Path.EndsWith(".daily", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldRejectInvalidTimezone()
    {
        var config = CreateValidConfig();
        config.Jobs[0].Timezone = "Mars/Olympus";

        var result = new ConfigValidator().Validate(config);

        Assert.Contains(result.Errors, issue => issue.Path.EndsWith(".timezone", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldRejectDuplicateDestinationNames()
    {
        var config = CreateValidConfig();
        config.Jobs[0].Destinations.Add(new DestinationConfig
        {
            Name = "local",
            Kind = "local",
            Path = "/other",
        });

        var result = new ConfigValidator().Validate(config);

        Assert.Contains(result.Errors, issue => issue.Message.Contains("duplicate destination", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldRejectSftpWithoutPasswordOrKey()
    {
        var config = CreateValidConfig();
        config.Jobs[0].Destinations =
        [
            new DestinationConfig
            {
                Name = "nas",
                Kind = "sftp",
                Host = "nas.local",
                User = "backup",
            },
        ];

        var result = new ConfigValidator().Validate(config);

        Assert.Contains(result.Errors, issue => issue.Message.Contains("password or key_file", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldRejectInvalidNotifyUrl()
    {
        var config = CreateValidConfig();
        config.Jobs[0].Notify =
        [
            new NotifyConfig { Kind = "webhook", Url = "not-a-url" },
        ];

        var result = new ConfigValidator().Validate(config);

        Assert.Contains(result.Errors, issue => issue.Path.EndsWith(".url", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldAllowDisabledJobWithoutSourceAndDestinations()
    {
        var config = CreateValidConfig();
        config.Jobs[0].Enabled = false;
        config.Jobs[0].Source.Path = "";
        config.Jobs[0].Destinations.Clear();

        var result = new ConfigValidator().Validate(config);

        Assert.True(result.IsValid);
    }

    private static RootConfig CreateValidConfig() => new()
    {
        Version = 1,
        Jobs =
        [
            new JobConfig
            {
                Name = "photos",
                Schedule = "0 2 * * *",
                Source = new SourceConfig { Path = "/backup/data" },
                Destinations =
                [
                    new DestinationConfig
                    {
                        Name = "local",
                        Kind = "local",
                        Path = "/backups",
                    },
                ],
            },
        ],
    };
}
