using System.Globalization;

namespace CronBackup.Core.Retention;

/// <summary>
/// 归档 key 解析。文件名形如 backup_20261111_063000_123.tar.gz，
/// 时间戳是 job 时区的本地时间，定宽格式，字典序即时间序。
/// </summary>
public static class ArchiveKey
{
    private const string Prefix = "backup_";

    // 先匹配更长的后缀，避免将来增加别的 .tar 变体时误判。
    private static readonly string[] Suffixes = { ".tar.gz", ".tar" };

    private static readonly string[] TimestampFormats =
    {
        "yyyyMMdd_HHmmss_fff", // v2 归档
        "yyyyMMdd_HHmmss",     // 兼容历史文件名
    };

    public static string FileName(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash >= 0 ? key[(slash + 1)..] : key;
    }

    /// <summary>解析 key 中的本地时间戳；解析失败返回 false（调用方回退到 Modified）。</summary>
    public static bool TryGetTimestamp(string key, out DateTime local)
    {
        local = default;

        var name = FileName(key);
        if (!name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = Suffixes.FirstOrDefault(item => name.EndsWith(item, StringComparison.Ordinal));
        if (suffix is null)
        {
            return false;
        }

        var stamp = name[Prefix.Length..^suffix.Length];
        return DateTime.TryParseExact(
            stamp,
            TimestampFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out local);
    }

    /// <summary>排序用：定宽时间戳可以直接按字符串比较。</summary>
    public static string SortKey(string key) => key;
}
