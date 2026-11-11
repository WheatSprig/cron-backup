namespace CronBackup.Core.Rclone;

public interface IRcloneSecretObscurer
{
    Task<string> ObscureAsync(string secret, CancellationToken cancellationToken);
}
