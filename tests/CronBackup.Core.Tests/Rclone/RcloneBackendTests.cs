using CronBackup.Core.Configuration;
using CronBackup.Core.Execution;
using CronBackup.Core.Rclone;
using CronBackup.Core.Tests.TestHelpers;

namespace CronBackup.Core.Tests.Rclone;

public sealed class RcloneBackendTests
{
    [Fact]
    public async Task StatAsync_ShouldParseLsjsonObject()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(
            0,
            """{"Path":"daily/backup.tar.gz","Size":12,"ModTime":"2026-11-08T10:00:00Z","IsDir":false}""",
            "",
            false));
        await using var backend = await CreateBackend(runner);

        var result = await backend.StatAsync("daily/backup.tar.gz", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("daily/backup.tar.gz", result!.Key);
        Assert.Equal(12, result.Size);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero), result.Modified);
    }

    [Fact]
    public async Task StatAsync_ShouldReturnNullWhenFileIsMissing()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(4, "", "not found", false));
        await using var backend = await CreateBackend(runner);

        Assert.Null(await backend.StatAsync("daily/missing.tar.gz", CancellationToken.None));
    }

    [Fact]
    public async Task ListAsync_ShouldPrefixReturnedPaths()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(
            0,
            """[{"Path":"one.tar.gz","Size":1,"ModTime":"2026-11-08T10:00:00Z","IsDir":false},{"Path":"two.tar.gz","Size":2,"ModTime":"2026-11-08T11:00:00Z","IsDir":false}]""",
            "",
            false));
        await using var backend = await CreateBackend(runner);

        var result = await backend.ListAsync("daily", CancellationToken.None);

        Assert.Equal(new[] { "daily/one.tar.gz", "daily/two.tar.gz" }, result.Select(item => item.Key));
        Assert.Contains(
            runner.Specs,
            spec => spec.Arguments.Contains("lsjson") &&
                    spec.Arguments.Contains("cb_test_s3:backups/photos/daily"));
    }

    [Fact]
    public async Task GetHashAsync_ShouldParseHashsum()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(
            0,
            "8d777f385d3dfec8815d20f7496026dc  daily/backup.tar.gz\n",
            "",
            false));
        await using var backend = await CreateBackend(runner);

        var hash = await backend.GetHashAsync("daily/backup.tar.gz", CancellationToken.None);

        Assert.Equal("8d777f385d3dfec8815d20f7496026dc", hash);
        Assert.Contains(runner.Specs, spec => spec.Arguments.Contains("hashsum"));
    }

    [Fact]
    public async Task UploadAsync_ShouldUseCopyToAndRenderedPath()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, "", "", false));
        await using var backend = await CreateBackend(runner);

        await backend.UploadAsync("local.tar.gz", "daily/backup.tar.gz", CancellationToken.None);

        var spec = Assert.Single(runner.Specs);
        Assert.Contains("copyto", spec.Arguments);
        Assert.Contains("local.tar.gz", spec.Arguments);
        Assert.Contains("cb_test_s3:backups/photos/daily/backup.tar.gz", spec.Arguments);
        Assert.Contains("--config", spec.Arguments);
    }

    [Fact]
    public async Task DeleteManyAsync_ShouldIgnoreMissingFiles()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(4, "", "not found", false));
        await using var backend = await CreateBackend(runner);

        await backend.DeleteManyAsync(["daily/missing.tar.gz"], CancellationToken.None);
    }

    [Fact]
    public async Task Factory_ShouldRenderAndReturnRcloneBackend()
    {
        using var temp = new TempDirectory();
        var runner = new StubProcessRunner(_ => new ProcessResult(0, "[]", "", false));
        var factory = new RcloneStorageBackendFactory(
            new RcloneConfRenderer(),
            new FakeSecretObscurer(),
            runner,
            temporaryRoot: temp.Path);
        var job = new JobConfig
        {
            Name = "photos",
            Destinations =
            [
                new DestinationConfig
                {
                    Name = "sftp",
                    Kind = "sftp",
                    Host = "nas.local",
                    User = "backup",
                    Password = "secret",
                    Root = "/backup",
                },
            ],
        };

        var backend = await factory.CreateAsync(job, job.Destinations[0], CancellationToken.None);

        Assert.Equal("sftp", backend.Name);
        Assert.False(backend.SupportsServerSideCopy);
        Assert.Single(Directory.GetFiles(temp.Path, "rclone.conf", SearchOption.AllDirectories));

        await backend.DisposeAsync();
        Assert.Empty(Directory.GetFiles(temp.Path, "rclone.conf", SearchOption.AllDirectories));
    }

    private static async Task<RcloneBackend> CreateBackend(IProcessRunner runner)
    {
        var conf = await RcloneConfFile.CreateAsync("[cb_test_s3]\ntype = s3\n", null, CancellationToken.None);
        return new RcloneBackend(
            "s3",
            runner,
            "rclone",
            conf,
            new RenderedRcloneDestination("photos", "s3", "cb_test_s3", "cb_test_s3:backups/photos"),
            supportsServerSideCopy: true);
    }
}
