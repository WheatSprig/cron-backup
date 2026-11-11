using CronBackup.Core.Configuration;

namespace CronBackup.Core.Storage;

public interface IStorageBackendFactory
{
    Task<IStorageBackend> CreateAsync(
        JobConfig job,
        DestinationConfig destination,
        CancellationToken cancellationToken);
}

public sealed class DefaultStorageBackendFactory : IStorageBackendFactory
{
    public Task<IStorageBackend> CreateAsync(
        JobConfig job,
        DestinationConfig destination,
        CancellationToken cancellationToken)
    {
        if (destination.Kind.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<IStorageBackend>(new LocalBackend(
                destination.Path ??
                throw new ConfigException(
                    $"jobs.{job.Name}.destinations.{destination.Name}.path",
                    "local destination requires a path")));
        }

        throw new NotSupportedException(
            $"backend '{destination.Kind}' is not available in this build yet");
    }
}
