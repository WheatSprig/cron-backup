using CronBackup.Core.Storage;
using CronBackup.Core.Tests.TestHelpers;

namespace CronBackup.Core.Tests.Storage;

public sealed class LocalBackendTests
{
    [Fact]
    public async Task UploadAndStat_ShouldStoreFileAndMetadata()
    {
        using var temp = new TempDirectory();
        var source = temp.File("source.tar.gz");
        await File.WriteAllTextAsync(source, "archive");
        var backend = new LocalBackend(Path.Combine(temp.Path, "remote"));

        await backend.UploadAsync(source, "daily/backup.tar.gz", CancellationToken.None);
        var stat = await backend.StatAsync("daily/backup.tar.gz", CancellationToken.None);

        Assert.NotNull(stat);
        Assert.Equal("daily/backup.tar.gz", stat!.Key);
        Assert.Equal(7, stat.Size);
        Assert.Equal(
            new DateTimeOffset(File.GetLastWriteTimeUtc(source), TimeSpan.Zero),
            stat.Modified);
    }

    [Fact]
    public async Task ListAsync_ShouldReturnOnlyMatchingPrefix()
    {
        using var temp = new TempDirectory();
        var source = temp.File("source.txt");
        await File.WriteAllTextAsync(source, "data");
        var backend = new LocalBackend(Path.Combine(temp.Path, "remote"));

        await backend.UploadAsync(source, "daily/one.txt", CancellationToken.None);
        await backend.UploadAsync(source, "daily/two.txt", CancellationToken.None);
        await backend.UploadAsync(source, "weekly/three.txt", CancellationToken.None);

        var daily = await backend.ListAsync("daily", CancellationToken.None);
        var all = await backend.ListAsync("", CancellationToken.None);

        Assert.Equal(new[] { "daily/one.txt", "daily/two.txt" }, daily.Select(item => item.Key));
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task CopyWithinAsync_ShouldCopyContentAndModificationTime()
    {
        using var temp = new TempDirectory();
        var source = temp.File("source.txt");
        await File.WriteAllTextAsync(source, "data");
        var backend = new LocalBackend(Path.Combine(temp.Path, "remote"));
        await backend.UploadAsync(source, "daily/backup.txt", CancellationToken.None);

        await backend.CopyWithinAsync("daily/backup.txt", "weekly/backup.txt", CancellationToken.None);

        var original = await backend.StatAsync("daily/backup.txt", CancellationToken.None);
        var copy = await backend.StatAsync("weekly/backup.txt", CancellationToken.None);
        Assert.NotNull(copy);
        Assert.Equal(original!.Modified, copy!.Modified);
    }

    [Fact]
    public async Task DownloadAsync_ShouldWriteFile()
    {
        using var temp = new TempDirectory();
        var source = temp.File("source.txt");
        await File.WriteAllTextAsync(source, "data");
        var backend = new LocalBackend(Path.Combine(temp.Path, "remote"));
        await backend.UploadAsync(source, "daily/backup.txt", CancellationToken.None);
        var destination = temp.File("restore/backup.txt");

        await backend.DownloadAsync("daily/backup.txt", destination, CancellationToken.None);

        Assert.Equal("data", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task DeleteManyAsync_ShouldBeIdempotent()
    {
        using var temp = new TempDirectory();
        var source = temp.File("source.txt");
        await File.WriteAllTextAsync(source, "data");
        var backend = new LocalBackend(Path.Combine(temp.Path, "remote"));
        await backend.UploadAsync(source, "daily/backup.txt", CancellationToken.None);

        await backend.DeleteManyAsync(new[] { "daily/backup.txt", "daily/missing.txt" }, CancellationToken.None);
        await backend.DeleteManyAsync(new[] { "daily/backup.txt" }, CancellationToken.None);

        Assert.Null(await backend.StatAsync("daily/backup.txt", CancellationToken.None));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("daily/../../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("")]
    public async Task Resolve_ShouldRejectUnsafeKeys(string key)
    {
        using var temp = new TempDirectory();
        var source = temp.File("source.txt");
        await File.WriteAllTextAsync(source, "data");
        var backend = new LocalBackend(Path.Combine(temp.Path, "remote"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => backend.UploadAsync(source, key, CancellationToken.None));
    }

    [Fact]
    public async Task UploadAsync_ShouldOverwriteExistingObject()
    {
        using var temp = new TempDirectory();
        var first = temp.File("first.txt");
        var second = temp.File("second.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        var backend = new LocalBackend(Path.Combine(temp.Path, "remote"));

        await backend.UploadAsync(first, "daily/backup.txt", CancellationToken.None);
        await backend.UploadAsync(second, "daily/backup.txt", CancellationToken.None);
        var destination = temp.File("download.txt");
        await backend.DownloadAsync("daily/backup.txt", destination, CancellationToken.None);

        Assert.Equal("second", await File.ReadAllTextAsync(destination));
    }
}
