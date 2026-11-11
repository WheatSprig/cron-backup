using CronBackup.Core.Configuration;

namespace CronBackup.Core.Storage;

public interface IStorageBackendFactory
{
    IStorageBackend Create(JobConfig job, DestinationConfig destination);
}

public sealed class DefaultStorageBackendFactory : IStorageBackendFactory
{
    public IStorageBackend Create(JobConfig job, DestinationConfig destination)
    {
        return destination.Kind.ToLowerInvariant() switch
        {
            "local" => new LocalBackend(
                destination.Path ??
                throw new ConfigException(
                    $"jobs.{job.Name}.destinations.{destination.Name}.path",
                    "local destination requires a path")),
            "s3" or "sftp" or "rclone" => throw new NotSupportedException(
                $"backend '{destination.Kind}' is not available in this build yet"),
            _ => throw new ConfigException(
                $"jobs.{job.Name}.destinations.{destination.Name}.kind",
                $"unsupported destination kind '{destination.Kind}'"),
        };
    }
}
