using CronBackup.Core.Execution;
using CronBackup.Core.Tests.TestHelpers;

namespace CronBackup.Core.Tests.Execution;

public sealed class ArchiveServiceTests
{
    [Fact]
    public async Task CreateAsync_ShouldCreateTarGzAndCleanUp()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "data.txt"), "hello");

        var service = new TarArchiveService(new SystemProcessRunner());
        var result = await service.CreateAsync(
            new ArchiveRequest("photos", source, temp.Path, DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.True(File.Exists(result.ArchivePath));
        Assert.True(result.Size > 0);
        Assert.StartsWith("backup_", result.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".tar.gz", result.FileName, StringComparison.Ordinal);

        var directory = result.DirectoryPath;
        await result.DisposeAsync();
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectMissingSource()
    {
        using var temp = new TempDirectory();
        var service = new TarArchiveService(new SystemProcessRunner());

        await Assert.ThrowsAsync<ArchiveException>(() => service.CreateAsync(
            new ArchiveRequest("photos", Path.Combine(temp.Path, "missing"), temp.Path, DateTimeOffset.UtcNow),
            CancellationToken.None));
    }
}
