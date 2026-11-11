using CronBackup.Core.Rclone;

namespace CronBackup.Core.Tests.TestHelpers;

public sealed class FakeSecretObscurer : IRcloneSecretObscurer
{
    public List<string> Secrets { get; } = new();

    public Task<string> ObscureAsync(string secret, CancellationToken cancellationToken)
    {
        Secrets.Add(secret);
        return Task.FromResult($"OBSCURED({secret.Length})");
    }
}
