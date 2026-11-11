namespace CronBackup.Core.Execution;

public static class TempWorkspace
{
    public static string Root => Path.Combine(Path.GetTempPath(), "cron-backup");

    // 进程被 kill 时 Dispose 不会执行，临时归档和 rclone.conf 会残留。
    // 启动时按天级年龄清扫，但绝不碰 locks/ 和 status.json。
    public static IReadOnlyList<string> CleanupStale(
        TimeSpan maxAge,
        string? root = null,
        DateTimeOffset? now = null)
    {
        var directory = root ?? Root;
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        var cutoff = (now ?? DateTimeOffset.UtcNow) - maxAge;
        var removed = new List<string>();

        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(child);
            if (string.Equals(name, "locks", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "status.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var lastWrite = File.GetLastWriteTimeUtc(child);
                if (lastWrite >= cutoff.UtcDateTime)
                {
                    continue;
                }

                if (Directory.Exists(child))
                {
                    Directory.Delete(child, recursive: true);
                }
                else
                {
                    File.Delete(child);
                }

                removed.Add(child);
            }
            catch (IOException)
            {
                // 另一个进程可能正在使用；下一轮再清。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }
}
