using CronBackup.Core.Configuration;

namespace CronBackup.Core.Execution;

/// <summary>
/// 归档格式与文件名约定。tar.gz 会压缩；tar 只打包，适合源目录里已经是
/// gzip 压缩文件（例如 postgres-backup-local 产出的 .sql.gz）的场景。
/// </summary>
public static class ArchiveNaming
{
    public const string TarGz = "tar.gz";
    public const string Tar = "tar";

    public static string Normalize(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return TarGz;
        }

        return format.Trim().ToLowerInvariant() switch
        {
            TarGz => TarGz,
            Tar => Tar,
            _ => throw new ConfigException(
                "archive.format",
                $"unsupported archive format '{format}'"),
        };
    }

    public static string FileName(DateTimeOffset timestamp, string? format) =>
        $"backup_{timestamp:yyyyMMdd_HHmmss_fff}{Extension(format)}";

    public static string Extension(string? format) =>
        Normalize(format) == Tar ? ".tar" : ".tar.gz";
}
