namespace CronBackup.Core.Tests.TestHelpers;

public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "cron-backup-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relativePath)
    {
        var fullPath = System.IO.Path.Combine(Path, relativePath);
        var parent = System.IO.Path.GetDirectoryName(fullPath);
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }

        return fullPath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Test cleanup is best effort on Windows.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
