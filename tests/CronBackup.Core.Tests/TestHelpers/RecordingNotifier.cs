using CronBackup.Core.Configuration;
using CronBackup.Core.Execution;

namespace CronBackup.Core.Tests.TestHelpers;

public sealed class RecordingNotifier : INotifier
{
    public List<(NotificationPhase Phase, NotificationMessage Message)> Calls { get; } = new();

    public Task<IReadOnlyList<NotificationResult>> SendAsync(
        IReadOnlyList<NotifyConfig>? targets,
        NotificationPhase phase,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        Calls.Add((phase, message));
        return Task.FromResult<IReadOnlyList<NotificationResult>>(Array.Empty<NotificationResult>());
    }
}
