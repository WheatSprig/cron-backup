namespace CronBackup.Core.Execution;

public sealed record ArchiveRequest(
    string JobName,
    string SourcePath,
    string TempDirectory,
    DateTimeOffset Timestamp);

public sealed class ArchiveResult : IAsyncDisposable
{
    private bool _disposed;

    public ArchiveResult(string directoryPath, string archivePath, long size)
    {
        DirectoryPath = directoryPath;
        ArchivePath = archivePath;
        Size = size;
    }

    public string DirectoryPath { get; }

    public string ArchivePath { get; }

    public long Size { get; }

    public string FileName => Path.GetFileName(ArchivePath);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; the caller may be shutting down.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class ArchiveException : Exception
{
    public ArchiveException(string message)
        : base(message)
    {
    }
}

public interface IArchiveService
{
    Task<ArchiveResult> CreateAsync(ArchiveRequest request, CancellationToken cancellationToken);
}

public sealed class TarArchiveService : IArchiveService
{
    private readonly IProcessRunner _processRunner;
    private readonly string _tarPath;

    public TarArchiveService(IProcessRunner processRunner, string tarPath = "tar")
    {
        _processRunner = processRunner;
        _tarPath = tarPath;
    }

    public async Task<ArchiveResult> CreateAsync(
        ArchiveRequest request,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(request.SourcePath))
        {
            throw new ArchiveException($"source directory not found: {request.SourcePath}");
        }

        var timestamp = request.Timestamp.ToString("yyyyMMdd_HHmmss");
        var archiveName = $"backup_{timestamp}.tar.gz";
        var directory = Path.Combine(
            request.TempDirectory,
            "cron-backup",
            Sanitize(request.JobName),
            $"{timestamp}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        var archivePath = Path.Combine(directory, archiveName);
        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessSpec(
                    _tarPath,
                    ["-czf", archivePath, "-C", request.SourcePath, "."],
                    Timeout: TimeSpan.FromHours(6)),
                cancellationToken);

            if (result.TimedOut)
            {
                throw new ArchiveException("tar timed out");
            }

            if (result.ExitCode != 0)
            {
                throw new ArchiveException(
                    $"tar failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
            }

            var info = new FileInfo(archivePath);
            if (!info.Exists)
            {
                throw new ArchiveException("tar did not produce an archive");
            }

            return new ArchiveResult(directory, archivePath, info.Length);
        }
        catch
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
            }

            throw;
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
