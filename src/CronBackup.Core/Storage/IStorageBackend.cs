namespace CronBackup.Core.Storage;

public interface IStorageBackend : IAsyncDisposable
{
    string Name { get; }

    bool SupportsServerSideCopy { get; }

    Task<RemoteObject?> StatAsync(string key, CancellationToken cancellationToken);

    Task<string?> GetHashAsync(string key, CancellationToken cancellationToken);

    Task UploadAsync(string localPath, string key, CancellationToken cancellationToken);

    Task CopyWithinAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteObject>> ListAsync(string prefix, CancellationToken cancellationToken);

    Task DownloadAsync(string key, string localPath, CancellationToken cancellationToken);

    Task DeleteManyAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken);
}
