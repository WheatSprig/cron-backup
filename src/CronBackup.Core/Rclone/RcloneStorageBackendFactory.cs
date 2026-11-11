using CronBackup.Core.Configuration;
using CronBackup.Core.Execution;
using CronBackup.Core.Storage;

namespace CronBackup.Core.Rclone;

public sealed class RcloneStorageBackendFactory : IStorageBackendFactory
{
    private readonly RcloneConfRenderer _renderer;
    private readonly IRcloneSecretObscurer _obscurer;
    private readonly IProcessRunner _processRunner;
    private readonly string _rclonePath;
    private readonly string? _temporaryRoot;

    public RcloneStorageBackendFactory(
        RcloneConfRenderer renderer,
        IRcloneSecretObscurer obscurer,
        IProcessRunner processRunner,
        string rclonePath = "rclone",
        string? temporaryRoot = null)
    {
        _renderer = renderer;
        _obscurer = obscurer;
        _processRunner = processRunner;
        _rclonePath = rclonePath;
        _temporaryRoot = temporaryRoot;
    }

    public async Task<IStorageBackend> CreateAsync(
        JobConfig job,
        DestinationConfig destination,
        CancellationToken cancellationToken)
    {
        if (destination.Kind.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalBackend(
                destination.Path ??
                throw new ConfigException(
                    $"jobs.{job.Name}.destinations.{destination.Name}.path",
                    "local destination requires a path"));
        }

        // 只为当前 job 渲染 section；backend 持有 conf 文件，Dispose 时删除。
        // 多个 destination 各自持有一份临时 conf，生命周期互不影响。
        var renderResult = await _renderer.RenderAsync(
            new RootConfig { Jobs = [job] },
            _obscurer,
            cancellationToken);

        var rendered = renderResult.Destinations.FirstOrDefault(item =>
            string.Equals(item.DestinationName, destination.Name, StringComparison.Ordinal));
        if (rendered is null)
        {
            throw new ConfigException(
                $"jobs.{job.Name}.destinations.{destination.Name}",
                "could not render an rclone backend section");
        }

        var confFile = await RcloneConfFile.CreateAsync(
            renderResult.Content,
            _temporaryRoot,
            cancellationToken);

        try
        {
            return new RcloneBackend(
                destination.Name,
                _processRunner,
                _rclonePath,
                confFile,
                rendered,
                supportsServerSideCopy: destination.Kind.Equals("s3", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            confFile.Dispose();
            throw;
        }
    }
}
