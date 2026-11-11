using CronBackup.Core.Configuration;
using CronBackup.Core.Rclone;
using CronBackup.Core.Tests.TestHelpers;

namespace CronBackup.Core.Tests.Rclone;

public sealed class RcloneConfRendererTests
{
    [Fact]
    public async Task RenderAsync_ShouldRenderS3Section()
    {
        var config = CreateConfig();
        var obscurer = new FakeSecretObscurer();

        var result = await new RcloneConfRenderer().RenderAsync(config, obscurer, CancellationToken.None);

        Assert.Contains("[cb_photos_s3_main]", result.Content);
        Assert.Contains("type = s3", result.Content);
        Assert.Contains("provider = Minio", result.Content);
        Assert.Contains("access_key_id = key", result.Content);
        Assert.Contains("secret_access_key = secret", result.Content);
        Assert.Contains("no_check_bucket = true", result.Content);
        Assert.DoesNotContain("type = local", result.Content);
    }

    [Fact]
    public async Task RenderAsync_ShouldObscureSftpPassword()
    {
        var config = CreateConfig();
        var obscurer = new FakeSecretObscurer();

        var result = await new RcloneConfRenderer().RenderAsync(config, obscurer, CancellationToken.None);

        Assert.Contains("pass = OBSCURED(11)", result.Content);
        Assert.DoesNotContain("sftp-secret", result.Content);
        Assert.Contains("sftp-secret", obscurer.Secrets);
    }

    [Fact]
    public async Task RenderAsync_ShouldObscureKnownRcloneOptions()
    {
        var config = new RootConfig
        {
            Jobs =
            [
                new JobConfig
                {
                    Name = "drive",
                    Destinations =
                    [
                        new DestinationConfig
                        {
                            Name = "gdrive",
                            Kind = "rclone",
                            Type = "drive",
                            Options = new Dictionary<string, string>
                            {
                                ["client_id"] = "id",
                                ["client_secret"] = "secret",
                                ["token"] = "token",
                            },
                        },
                    ],
                },
            ],
        };
        var obscurer = new FakeSecretObscurer();

        var result = await new RcloneConfRenderer().RenderAsync(config, obscurer, CancellationToken.None);

        Assert.Contains("client_id = id", result.Content);
        Assert.Contains("client_secret = OBSCURED(6)", result.Content);
        Assert.Contains("token = token", result.Content);
        Assert.DoesNotContain("client_secret = secret", result.Content);
    }

    [Fact]
    public async Task RenderAsync_ShouldBuildDeterministicRemotePaths()
    {
        var config = CreateConfig();

        var result = await new RcloneConfRenderer().RenderAsync(
            config,
            new FakeSecretObscurer(),
            CancellationToken.None);

        var s3 = Assert.Single(result.Destinations, item => item.DestinationName == "s3-main");
        Assert.Equal("cb_photos_s3_main:backups/photos", s3.BasePath);
        Assert.Equal(
            "cb_photos_s3_main:backups/photos/daily/backup.tar.gz",
            s3.BuildObjectPath("/daily/backup.tar.gz"));

        var sftp = Assert.Single(result.Destinations, item => item.DestinationName == "nas");
        Assert.Equal("cb_photos_nas:/volume1/photos", sftp.BasePath);
    }

    [Fact]
    public async Task RenderAsync_ShouldMakeSanitizedSectionNamesUnique()
    {
        var config = new RootConfig
        {
            Jobs =
            [
                new JobConfig
                {
                    Name = "job",
                    Destinations =
                    [
                        new DestinationConfig
                        {
                            Name = "s3-main",
                            Kind = "s3",
                            Bucket = "one",
                            Provider = "AWS",
                        },
                        new DestinationConfig { Name = "s3_main", Kind = "s3", Bucket = "bucket", Provider = "AWS" },
                    ],
                },
            ],
        };

        var result = await new RcloneConfRenderer().RenderAsync(
            config,
            new FakeSecretObscurer(),
            CancellationToken.None);

        Assert.Contains("[cb_job_s3_main]", result.Content);
        Assert.Contains("[cb_job_s3_main_2]", result.Content);
    }

    [Fact]
    public async Task RenderAsync_ShouldRejectNewlines()
    {
        var config = CreateConfig();
        config.Jobs[0].Destinations[0].Endpoint = "https://example.com\ninjected=true";

        await Assert.ThrowsAsync<ConfigException>(() => new RcloneConfRenderer().RenderAsync(
            config,
            new FakeSecretObscurer(),
            CancellationToken.None));
    }

    [Fact]
    public async Task RcloneConfFile_ShouldWriteAndDeleteTemporaryFile()
    {
        var temp = new TempDirectory();
        try
        {
            var file = await RcloneConfFile.CreateAsync("[remote]\ntype = local\n", temp.Path, CancellationToken.None);
            var directory = file.DirectoryPath;

            Assert.True(File.Exists(file.FilePath));
            Assert.Equal("[remote]\ntype = local\n", await File.ReadAllTextAsync(file.FilePath));

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(file.FilePath);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
            }

            file.Dispose();

            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            temp.Dispose();
        }
    }

    private static RootConfig CreateConfig() => new()
    {
        Jobs =
        [
            new JobConfig
            {
                Name = "photos",
                Destinations =
                [
                    new DestinationConfig
                    {
                        Name = "s3-main",
                        Kind = "s3",
                        Provider = "Minio",
                        Endpoint = "https://s3.example.com",
                        Region = "us-east-1",
                        Bucket = "backups",
                        Root = "photos",
                        AccessKeyId = "key",
                        SecretAccessKey = "secret",
                        NoCheckBucket = true,
                    },
                    new DestinationConfig
                    {
                        Name = "nas",
                        Kind = "sftp",
                        Host = "nas.local",
                        User = "backup",
                        Password = "sftp-secret",
                        Root = "/volume1/photos",
                    },
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
