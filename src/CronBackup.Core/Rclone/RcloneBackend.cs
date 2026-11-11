using System.Globalization;
using System.Text.Json;
using CronBackup.Core.Execution;
using CronBackup.Core.Storage;

namespace CronBackup.Core.Rclone;

public sealed class RcloneException : Exception
{
    public RcloneException(string message)
        : base(message)
    {
    }
}

public sealed class RcloneBackend : IStorageBackend
{
    private readonly IProcessRunner _processRunner;
    private readonly string _rclonePath;
    private readonly string _confPath;
    private readonly RenderedRcloneDestination _destination;
    private readonly RcloneConfFile _confFile;
    private readonly bool _supportsServerSideCopy;
    private bool _disposed;

    public RcloneBackend(
        string name,
        IProcessRunner processRunner,
        string rclonePath,
        RcloneConfFile confFile,
        RenderedRcloneDestination destination,
        bool supportsServerSideCopy)
    {
        Name = name;
        _processRunner = processRunner;
        _rclonePath = rclonePath;
        _confPath = confFile.FilePath;
        _confFile = confFile;
        _destination = destination;
        _supportsServerSideCopy = supportsServerSideCopy;
    }

    public string Name { get; }

    public bool SupportsServerSideCopy => _supportsServerSideCopy;

    public async Task<RemoteObject?> StatAsync(string key, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            ["lsjson", "--stat", BuildPath(key)],
            cancellationToken);

        // rclone 用 3/4 表示目录/文件不存在；Stat 的语义就是“不存在返回 null”。
        if (result.ExitCode is 3 or 4)
        {
            return null;
        }

        EnsureSuccess(result, "lsjson --stat");
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var element = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().FirstOrDefault()
            : document.RootElement;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty("IsDir", out var isDir) && isDir.GetBoolean())
        {
            return null;
        }

        return ParseObject(element, key);
    }

    public async Task<string?> GetHashAsync(string key, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            ["hashsum", "MD5", BuildPath(key)],
            cancellationToken);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        var firstLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (firstLine is null)
        {
            return null;
        }

        var hash = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return hash.Equals("UNSUPPORTED", StringComparison.OrdinalIgnoreCase) ||
               hash.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            ? null
            : hash.ToLowerInvariant();
    }

    public async Task UploadAsync(string localPath, string key, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            ["copyto", localPath, BuildPath(key)],
            cancellationToken);
        EnsureSuccess(result, "copyto upload");
    }

    public async Task CopyWithinAsync(
        string sourceKey,
        string destinationKey,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            ["copyto", BuildPath(sourceKey), BuildPath(destinationKey)],
            cancellationToken);
        EnsureSuccess(result, "copyto server-side copy");
    }

    public async Task<IReadOnlyList<RemoteObject>> ListAsync(
        string prefix,
        CancellationToken cancellationToken)
    {
        // lsjson 返回的 Path 相对于被列出的目录，所以按 prefix 补齐成 backend 的完整 key。
        var normalizedPrefix = prefix.Replace('\\', '/').Trim('/');
        var remotePath = normalizedPrefix.Length == 0
            ? _destination.BasePath
            : _destination.BuildObjectPath(normalizedPrefix);
        var result = await RunAsync(
            ["lsjson", "--recursive", "--files-only", remotePath],
            cancellationToken);

        if (result.ExitCode is 3 or 4)
        {
            return Array.Empty<RemoteObject>();
        }

        EnsureSuccess(result, "lsjson list");
        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return Array.Empty<RemoteObject>();
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<RemoteObject>();
        }

        var objects = new List<RemoteObject>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("IsDir", out var isDir) && isDir.GetBoolean())
            {
                continue;
            }

            var path = GetString(element, "Path");
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var key = normalizedPrefix.Length == 0
                ? path
                : $"{normalizedPrefix}/{path}";
            objects.Add(ParseObject(element, key));
        }

        return objects;
    }

    public async Task DownloadAsync(
        string key,
        string localPath,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            ["copyto", BuildPath(key), localPath],
            cancellationToken);
        EnsureSuccess(result, "copyto download");
    }

    public async Task DeleteManyAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken)
    {
        foreach (var key in keys)
        {
            var result = await RunAsync(
                ["deletefile", BuildPath(key)],
                cancellationToken);

            if (result.ExitCode == 4)
            {
                continue;
            }

            EnsureSuccess(result, "deletefile");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _confFile.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private string BuildPath(string key) => _destination.BuildObjectPath(key);

    private async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var processArguments = new List<string>
        {
            "--config",
            _confPath,
            "--ask-password=false",
            "--retries",
            "3",
            "--low-level-retries",
            "10",
        };
        processArguments.AddRange(arguments);

        return await _processRunner.RunAsync(
            new ProcessSpec(_rclonePath, processArguments),
            cancellationToken);
    }

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.TimedOut)
        {
            throw new RcloneException($"rclone {operation} timed out");
        }

        if (result.ExitCode != 0)
        {
            throw new RcloneException(
                $"rclone {operation} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }
    }

    private static RemoteObject ParseObject(JsonElement element, string key)
    {
        var size = element.TryGetProperty("Size", out var sizeElement) &&
                   sizeElement.TryGetInt64(out var parsedSize)
            ? parsedSize
            : 0;

        var modified = DateTimeOffset.MinValue;
        var modifiedText = GetString(element, "ModTime");
        if (!string.IsNullOrEmpty(modifiedText) &&
            DateTimeOffset.TryParse(
                modifiedText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsedModified))
        {
            modified = parsedModified;
        }

        return new RemoteObject(key, size, modified);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }
        }

        return null;
    }
}
