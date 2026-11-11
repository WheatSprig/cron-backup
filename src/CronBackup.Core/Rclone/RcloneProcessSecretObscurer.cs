using System.Diagnostics;

namespace CronBackup.Core.Rclone;

public sealed class RcloneProcessSecretObscurer : IRcloneSecretObscurer
{
    private readonly string _rclonePath;

    public RcloneProcessSecretObscurer(string rclonePath = "rclone")
    {
        _rclonePath = rclonePath;
    }

    public async Task<string> ObscureAsync(string secret, CancellationToken cancellationToken)
    {
        if (secret.Contains('\r') || secret.Contains('\n'))
        {
            throw new ArgumentException("secret cannot contain a newline", nameof(secret));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _rclonePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("obscure");
        startInfo.ArgumentList.Add("-");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("failed to start rclone");

        try
        {
            // 密码只走 stdin，不出现在进程 argv 里；rclone obscure - 读第一行。
            await process.StandardInput.WriteLineAsync(secret);
            process.StandardInput.Close();

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            var output = await standardOutput;
            var error = await standardError;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"rclone obscure failed with exit code {process.ExitCode}: {error.Trim()}");
            }

            var obscured = output.Trim();
            if (obscured.Length == 0)
            {
                throw new InvalidOperationException("rclone obscure returned an empty value");
            }

            return obscured;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort during cancellation or startup failure.
        }
    }
}
