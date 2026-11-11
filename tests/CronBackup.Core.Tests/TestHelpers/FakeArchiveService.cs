using CronBackup.Core.Execution;

namespace CronBackup.Core.Tests.TestHelpers;

public sealed class FakeArchiveService : IArchiveService
{
    public List<ArchiveRequest> Requests { get; } = new();

    public Task<ArchiveResult> CreateAsync(ArchiveRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var directory = Path.Combine(
            request.TempDirectory,
            "fake-archive",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var archivePath = Path.Combine(
            directory,
            ArchiveNaming.FileName(request.Timestamp, request.Format));

        File.WriteAllText(archivePath, "fake archive contents");
        File.SetLastWriteTimeUtc(archivePath, request.Timestamp.UtcDateTime);
        return Task.FromResult(new ArchiveResult(directory, archivePath, new FileInfo(archivePath).Length));
    }
}
