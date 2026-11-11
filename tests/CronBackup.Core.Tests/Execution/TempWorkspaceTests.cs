using CronBackup.Core.Execution;
using CronBackup.Core.Tests.TestHelpers;

namespace CronBackup.Core.Tests.Execution;

public sealed class TempWorkspaceTests
{
    [Fact]
    public void CleanupStale_ShouldRemoveOldEntriesButKeepLocksAndStatus()
    {
        using var temp = new TempDirectory();
        var oldDirectory = Path.Combine(temp.Path, "old-archive");
        var oldFile = Path.Combine(temp.Path, "old-restore.tar.gz");
        var locks = Path.Combine(temp.Path, "locks");
        var status = Path.Combine(temp.Path, "status.json");
        Directory.CreateDirectory(oldDirectory);
        Directory.CreateDirectory(locks);
        File.WriteAllText(oldFile, "old");
        File.WriteAllText(status, "{}");

        var oldTime = DateTime.UtcNow.AddDays(-3);
        Directory.SetLastWriteTimeUtc(oldDirectory, oldTime);
        File.SetLastWriteTimeUtc(oldFile, oldTime);
        Directory.SetLastWriteTimeUtc(locks, oldTime);
        File.SetLastWriteTimeUtc(status, oldTime);

        var removed = TempWorkspace.CleanupStale(
            TimeSpan.FromDays(1),
            temp.Path,
            DateTimeOffset.UtcNow);

        Assert.Contains(oldDirectory, removed);
        Assert.Contains(oldFile, removed);
        Assert.False(Directory.Exists(oldDirectory));
        Assert.False(File.Exists(oldFile));
        Assert.True(Directory.Exists(locks));
        Assert.True(File.Exists(status));
    }
}
