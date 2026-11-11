using System.Text;

namespace CronBackup.Core.Rclone;

public sealed class RcloneConfFile : IDisposable
{
    private bool _disposed;

    private RcloneConfFile(string directoryPath, string filePath)
    {
        DirectoryPath = directoryPath;
        FilePath = filePath;
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    public static async Task<RcloneConfFile> CreateAsync(
        string content,
        string? temporaryRoot,
        CancellationToken cancellationToken)
    {
        var root = temporaryRoot ?? Path.GetTempPath();
        var directory = Path.Combine(root, "cron-backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var path = Path.Combine(directory, "rclone.conf");
        try
        {
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return new RcloneConfFile(directory, path);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryDeleteDirectory(DirectoryPath);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: the process may already be shutting down.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup; the temporary file is still 0600.
        }
    }
}
