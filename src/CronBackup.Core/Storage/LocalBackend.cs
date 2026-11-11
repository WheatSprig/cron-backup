namespace CronBackup.Core.Storage;

public sealed class LocalBackend : IStorageBackend
{
    private readonly string _root;
    private readonly string _rootWithSeparator;
    private readonly StringComparison _pathComparison;

    public LocalBackend(string root)
    {
        _root = Path.GetFullPath(root);
        _rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        Directory.CreateDirectory(_root);
    }

    public string Name => "local";

    public bool SupportsServerSideCopy => true;

    public Task<RemoteObject?> StatAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(key);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return Task.FromResult<RemoteObject?>(null);
        }

        return Task.FromResult<RemoteObject?>(ToRemoteObject(info));
    }

    public async Task UploadAsync(string localPath, string key, CancellationToken cancellationToken)
    {
        var target = Resolve(key);
        var parent = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException($"cannot determine parent directory for '{target}'");
        Directory.CreateDirectory(parent);

        var temporaryPath = Path.Combine(parent, $".{Path.GetFileName(target)}.tmp-{Guid.NewGuid():N}");
        try
        {
            await using (var source = new FileStream(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            File.Move(temporaryPath, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(localPath));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task CopyWithinAsync(
        string sourceKey,
        string destinationKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Resolve(sourceKey);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"source object not found: {sourceKey}", source);
        }

        var destination = Resolve(destinationKey);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"cannot determine parent directory for '{destination}'");
        Directory.CreateDirectory(parent);
        File.Copy(source, destination, overwrite: true);
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RemoteObject>> ListAsync(
        string prefix,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPrefix = NormalizePrefix(prefix);
        var objects = Directory
            .EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Select(ToRemoteObject)
            .Where(item => normalizedPrefix.Length == 0 ||
                           item.Key.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<RemoteObject>>(objects);
    }

    public Task DownloadAsync(string key, string localPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Resolve(key);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"object not found: {key}", source);
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(localPath));
        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }

        File.Copy(source, localPath, overwrite: true);
        return Task.CompletedTask;
    }

    public Task DeleteManyAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Resolve(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private RemoteObject ToRemoteObject(FileInfo info)
    {
        var key = Path.GetRelativePath(_root, info.FullName).Replace('\\', '/');
        return new RemoteObject(
            key,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private string Resolve(string key)
    {
        if (key.StartsWith('/') || key.StartsWith('\\'))
        {
            throw new ArgumentException($"object key must be relative: {key}", nameof(key));
        }

        var normalized = key.Replace('\\', '/');
        if (normalized.Length == 0)
        {
            throw new ArgumentException("object key cannot be empty", nameof(key));
        }

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException($"invalid object key: {key}", nameof(key));
        }

        var fullPath = Path.GetFullPath(
            Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar)));

        if (!fullPath.StartsWith(_rootWithSeparator, _pathComparison) &&
            !string.Equals(fullPath, _root, _pathComparison))
        {
            throw new ArgumentException($"object key escapes root: {key}", nameof(key));
        }

        return fullPath;
    }

    private static string NormalizePrefix(string prefix)
    {
        var normalized = prefix.Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? string.Empty : normalized + "/";
    }
}
