using System.Diagnostics;

namespace CronBackup.Core.Execution;

public sealed class JobLockResult : IAsyncDisposable
{
    private readonly FileStream? _stream;

    private JobLockResult(bool acquired, FileStream? stream, string lockPath)
    {
        Acquired = acquired;
        _stream = stream;
        LockPath = lockPath;
    }

    public bool Acquired { get; }

    public string LockPath { get; }

    public static JobLockResult NotAcquired(string lockPath) => new(false, null, lockPath);

    public static JobLockResult AcquiredLock(FileStream stream, string lockPath) =>
        new(true, stream, lockPath);

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public interface IJobLockFactory
{
    Task<JobLockResult> TryAcquireAsync(string jobName, CancellationToken cancellationToken);
}

public sealed class FileJobLockFactory : IJobLockFactory
{
    private readonly string _lockDirectory;

    public FileJobLockFactory(string? lockDirectory = null)
    {
        _lockDirectory = lockDirectory ??
            Path.Combine(Path.GetTempPath(), "cron-backup", "locks");
    }

    public Task<JobLockResult> TryAcquireAsync(string jobName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_lockDirectory);
        var path = Path.Combine(_lockDirectory, Sanitize(jobName) + ".lock");

        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.WriteLine($"{Environment.ProcessId} {Process.GetCurrentProcess().StartTime:O}");
                writer.Flush();
            }

            stream.Position = 0;
            return Task.FromResult(JobLockResult.AcquiredLock(stream, path));
        }
        catch (IOException)
        {
            return Task.FromResult(JobLockResult.NotAcquired(path));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(JobLockResult.NotAcquired(path));
        }
    }

    private static string Sanitize(string value)
    {
        var sanitized = new string(value
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')
            .ToArray());
        return sanitized.Trim('_') is { Length: > 0 } result ? result : "job";
    }
}
