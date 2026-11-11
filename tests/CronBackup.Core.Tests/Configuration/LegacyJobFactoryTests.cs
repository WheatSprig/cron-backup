using CronBackup.Core.Configuration;

namespace CronBackup.Core.Tests.Configuration;

public sealed class LegacyJobFactoryTests
{
    [Fact]
    public void Create_ShouldMapS3RemoteAndDefaults()
    {
        var environment = new Dictionary<string, string?>
        {
            ["BACKUP_SRC"] = "/backup/data",
            ["RCLONE_REMOTE_NAMES"] = "backup",
            ["RCLONE_backup_TYPE"] = "s3",
            ["RCLONE_backup_ACCESS_KEY_ID"] = "key",
            ["RCLONE_backup_SECRET_ACCESS_KEY"] = "secret",
            ["RCLONE_PATH"] = "/daily/",
            ["CRON_SCHEDULE"] = "0 2 * * *",
        };

        var config = LegacyJobFactory.Create(environment);
        var job = Assert.Single(config.Jobs);
        var destination = Assert.Single(job.Destinations);

        Assert.Equal("default", job.Name);
        Assert.Equal("0 2 * * *", job.Schedule);
        Assert.Equal("/backup/data", job.Source.Path);
        Assert.Equal("s3", destination.Kind);
        Assert.Equal("backup", destination.Name);
        Assert.Equal("backups", destination.Bucket);
        Assert.Equal("AWS", destination.Provider);
        Assert.Equal("us-east-1", destination.Region);
        Assert.Equal("daily", destination.Root);
        Assert.Equal("key", destination.AccessKeyId);
        Assert.Equal("secret", destination.SecretAccessKey);
        Assert.True(destination.NoCheckBucket);
        Assert.False(destination.EnvAuth);
        Assert.False(job.Retention?.Enabled ?? true);
    }

    [Fact]
    public void Create_ShouldMapSftpRemote()
    {
        var environment = new Dictionary<string, string?>
        {
            ["RCLONE_REMOTE_NAMES"] = "nas",
            ["RCLONE_nas_TYPE"] = "sftp",
            ["RCLONE_nas_HOST"] = "nas.local",
            ["RCLONE_nas_USER"] = "backup",
            ["RCLONE_nas_PASS"] = "secret",
            ["RCLONE_nas_PORT"] = "2222",
            ["RCLONE_nas_PATH"] = "/volume1/backup",
        };

        var config = LegacyJobFactory.Create(environment);
        var destination = Assert.Single(config.Jobs[0].Destinations);

        Assert.Equal("sftp", destination.Kind);
        Assert.Equal("nas.local", destination.Host);
        Assert.Equal("backup", destination.User);
        Assert.Equal("secret", destination.Password);
        Assert.Equal(2222, destination.Port);
        Assert.Equal("volume1/backup", destination.Root);
    }

    [Fact]
    public void Create_ShouldMapDriveToRcloneEscapeHatch()
    {
        var environment = new Dictionary<string, string?>
        {
            ["RCLONE_REMOTE_NAMES"] = "gdrive",
            ["RCLONE_gdrive_TYPE"] = "drive",
            ["RCLONE_gdrive_CLIENT_ID"] = "id",
            ["RCLONE_gdrive_CLIENT_SECRET"] = "secret",
            ["RCLONE_gdrive_TOKEN"] = "token",
        };

        var config = LegacyJobFactory.Create(environment);
        var destination = Assert.Single(config.Jobs[0].Destinations);

        Assert.Equal("rclone", destination.Kind);
        Assert.Equal("drive", destination.Type);
        Assert.Equal("id", destination.Options?["client_id"]);
        Assert.Equal("secret", destination.Options?["client_secret"]);
        Assert.Equal("token", destination.Options?["token"]);
    }

    [Fact]
    public void Create_ShouldSupportMultipleRemotes()
    {
        var environment = new Dictionary<string, string?>
        {
            ["RCLONE_REMOTE_NAMES"] = "one,two",
            ["RCLONE_one_TYPE"] = "s3",
            ["RCLONE_one_BUCKET"] = "one-bucket",
            ["RCLONE_two_TYPE"] = "sftp",
            ["RCLONE_two_HOST"] = "two.local",
            ["RCLONE_two_USER"] = "two",
            ["RCLONE_two_PASS"] = "two-pass",
        };

        var config = LegacyJobFactory.Create(environment);

        Assert.Equal(2, config.Jobs[0].Destinations.Count);
        Assert.Equal("one-bucket", config.Jobs[0].Destinations[0].Bucket);
        Assert.Equal("two.local", config.Jobs[0].Destinations[1].Host);
    }

    [Fact]
    public void Create_ShouldTreatRootPathAsEmpty()
    {
        var environment = new Dictionary<string, string?>
        {
            ["RCLONE_REMOTE_NAMES"] = "backup",
            ["RCLONE_backup_TYPE"] = "sftp",
            ["RCLONE_backup_HOST"] = "host",
            ["RCLONE_backup_USER"] = "user",
            ["RCLONE_backup_PASS"] = "pass",
            ["RCLONE_PATH"] = "/",
        };

        var config = LegacyJobFactory.Create(environment);

        Assert.Equal(string.Empty, config.Jobs[0].Destinations[0].Root);
    }

    [Fact]
    public void Create_ShouldPreferPerRemotePathOverGlobalPath()
    {
        var environment = new Dictionary<string, string?>
        {
            ["RCLONE_REMOTE_NAMES"] = "backup",
            ["RCLONE_backup_TYPE"] = "sftp",
            ["RCLONE_backup_HOST"] = "host",
            ["RCLONE_backup_USER"] = "user",
            ["RCLONE_backup_PASS"] = "pass",
            ["RCLONE_backup_PATH"] = "/specific",
            ["RCLONE_PATH"] = "/global",
        };

        var config = LegacyJobFactory.Create(environment);

        Assert.Equal("specific", config.Jobs[0].Destinations[0].Root);
    }

    [Fact]
    public void Create_ShouldUseV1Defaults_WhenEnvironmentValuesAreEmpty()
    {
        var environment = new Dictionary<string, string?>
        {
            ["BACKUP_SRC"] = "",
            ["RCLONE_REMOTE_NAMES"] = "",
            ["RCLONE_PATH"] = "",
            ["RCLONE_backup_TYPE"] = "s3",
        };

        var config = LegacyJobFactory.Create(environment);
        var destination = Assert.Single(config.Jobs[0].Destinations);

        Assert.Equal("/backup/data", config.Jobs[0].Source.Path);
        Assert.Equal("backups", destination.Bucket);
        Assert.Equal("backups", destination.Root);
    }

    [Fact]
    public void Create_ShouldThrow_WhenRemoteTypeIsMissing()
    {
        var environment = new Dictionary<string, string?>
        {
            ["RCLONE_REMOTE_NAMES"] = "backup",
        };

        var exception = Assert.Throws<ConfigException>(() => LegacyJobFactory.Create(environment));

        Assert.Contains("RCLONE_backup_TYPE", exception.Message);
    }

    [Fact]
    public void Create_ShouldThrow_WhenBooleanIsInvalid()
    {
        var environment = new Dictionary<string, string?>
        {
            ["DRY_RUN"] = "maybe",
        };

        Assert.Throws<ConfigException>(() => LegacyJobFactory.Create(environment));
    }
}
