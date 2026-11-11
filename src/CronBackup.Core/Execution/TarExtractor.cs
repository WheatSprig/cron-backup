using System.Formats.Tar;
using System.IO.Compression;

namespace CronBackup.Core.Execution;

public static class TarExtractor
{
    public static async Task ExtractTarGzAsync(
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
        await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var reader = new TarReader(gzipStream, leaveOpen: false);

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
