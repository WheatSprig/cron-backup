using System.Formats.Tar;
using System.IO.Compression;

namespace CronBackup.Core.Execution;

public static class TarExtractor
{
    // 恢复的归档来自远端，不能信任条目路径。这里只接受目录和普通文件，
    // 每个条目都做 root 约束；符号链接/硬链接暂不支持，直接报错而不是冒险解压。
    // 通过文件头判断是否 gzip，兼容 .tar.gz 与 archive.format: tar。
    public static async Task ExtractAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        var destinationRoot = Path.GetFullPath(destination);
        Directory.CreateDirectory(destinationRoot);

        await using var fileStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzipStream = await TryOpenGzipAsync(fileStream, cancellationToken);
        using var reader = new TarReader((Stream?)gzipStream ?? fileStream, leaveOpen: true);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.Name.Replace('\\', '/');
            var relative = name.TrimStart('/');
            if (relative.Length == 0)
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(
                destinationRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(destinationRoot, target))
            {
                throw new InvalidOperationException($"archive entry escapes destination: {entry.Name}");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    var parent = Path.GetDirectoryName(target);
                    if (parent is not null)
                    {
                        Directory.CreateDirectory(parent);
                    }

                    entry.ExtractToFile(target, overwrite: false);
                    break;
                default:
                    throw new NotSupportedException(
                        $"archive entry type '{entry.EntryType}' is not supported for safe restore: {entry.Name}");
            }
        }
    }

    private static async Task<GZipStream?> TryOpenGzipAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[2];
        var read = await stream.ReadAsync(header.AsMemory(0, 2), cancellationToken);
        stream.Position = 0;

        return read == 2 && header[0] == 0x1f && header[1] == 0x8b
            ? new GZipStream(stream, CompressionMode.Decompress)
            : null;
    }

    private static bool IsWithin(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, comparison) ||
               string.Equals(candidate, root, comparison);
    }
}
