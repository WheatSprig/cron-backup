using CronBackup.Core.Configuration;

namespace CronBackup.Core.Retention;

public sealed record ResolvedRetention(
    bool Enabled,
    int Daily,
    int Weekly,
    int Monthly,
    int MaxDelete)
{
    public static ResolvedRetention Disabled { get; } = new(false, 0, 0, 0, 20);
}

public static class RetentionResolver
{
    // 逐字段覆盖：defaults < job < destination。job 上的 enabled:false 是硬关闭，
    // destination 不能反向开启；tier 用 0 表示“不管理这一层”。
    public static ResolvedRetention Resolve(
        RetentionPolicy? defaults,
        RetentionPolicy? job,
        RetentionPolicy? destination)
    {
        var enabled = true;
        var daily = 0;
        var weekly = 0;
        var monthly = 0;
        var maxDelete = 20;

        if (defaults is not null)
        {
            enabled = defaults.Enabled ?? enabled;
            daily = defaults.Daily ?? daily;
            weekly = defaults.Weekly ?? weekly;
            monthly = defaults.Monthly ?? monthly;
            maxDelete = defaults.MaxDelete ?? maxDelete;
        }

        if (job is not null)
        {
            if (job.Enabled == false)
            {
                return ResolvedRetention.Disabled;
            }

            enabled = job.Enabled ?? enabled;
            daily = job.Daily ?? daily;
            weekly = job.Weekly ?? weekly;
            monthly = job.Monthly ?? monthly;
            maxDelete = job.MaxDelete ?? maxDelete;
        }

        if (destination is not null)
        {
            if (destination.Enabled == false)
            {
                return ResolvedRetention.Disabled;
            }

            enabled = destination.Enabled ?? enabled;
            daily = destination.Daily ?? daily;
            weekly = destination.Weekly ?? weekly;
            monthly = destination.Monthly ?? monthly;
            maxDelete = destination.MaxDelete ?? maxDelete;
        }

        if (!enabled || (daily <= 0 && weekly <= 0 && monthly <= 0))
        {
            return ResolvedRetention.Disabled;
        }

        return new ResolvedRetention(
            true,
            Math.Max(0, daily),
            Math.Max(0, weekly),
            Math.Max(0, monthly),
            Math.Max(1, maxDelete));
    }
}
