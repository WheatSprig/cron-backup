using System.Diagnostics;

namespace CronBackup.Core.Execution;

public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? StandardInput = null,
    TimeSpan? Timeout = null);

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken);
}

public sealed class ProcessExecutionException : Exception
{
    public ProcessExecutionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            RedirectStandardInput = spec.StandardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory))
        {
            startInfo.WorkingDirectory = spec.WorkingDirectory;
        }

        if (spec.Environment is not null)
        {
            foreach (var item in spec.Environment)
            {
                startInfo.Environment[item.Key] = item.Value;
            }
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new ProcessExecutionException($"failed to start '{spec.FileName}'");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new ProcessExecutionException($"failed to start '{spec.FileName}': {exception.Message}", exception);
        }

        using (process)
        {
            if (spec.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(spec.StandardInput);
                process.StandardInput.Close();
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();

            using var timeoutSource = spec.Timeout is { } timeout
                ? new CancellationTokenSource(timeout)
                : null;
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource?.Token ?? CancellationToken.None);

            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(linkedSource.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested;
                TryKill(process);

                if (!timedOut)
                {
                    throw;
                }

                await process.WaitForExitAsync(CancellationToken.None);
            }

            var output = await standardOutput;
            var error = await standardError;

            return new ProcessResult(
                timedOut ? -1 : process.ExitCode,
                output,
                error,
                timedOut);
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
            // Best effort during cancellation or shutdown.
        }
    }
}
