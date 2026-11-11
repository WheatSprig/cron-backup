using CronBackup.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace CronBackup.Core.Execution;

public enum HookPhase
{
    Pre,
    Post,
}

public sealed record HookContext(
    string JobName,
    string SourcePath,
    string? ArchivePath,
    bool DryRun,
    TimeSpan DefaultTimeout);

public sealed record HookExecution(
    HookDefinition Hook,
    int ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

public sealed record HookRunResult(IReadOnlyList<HookExecution> Executions)
{
    public bool Succeeded => Executions.All(execution => execution.Succeeded);
}

public interface IHookRunner
{
    Task<HookRunResult> RunAsync(
        HookPhase phase,
        IReadOnlyList<HookDefinition>? hooks,
        HookContext context,
        bool stopOnFailure,
        CancellationToken cancellationToken);
}

public sealed class HookRunner : IHookRunner
{
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<HookRunner> _logger;

    public HookRunner(IProcessRunner processRunner, ILogger<HookRunner> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<HookRunResult> RunAsync(
        HookPhase phase,
        IReadOnlyList<HookDefinition>? hooks,
        HookContext context,
        bool stopOnFailure,
        CancellationToken cancellationToken)
    {
        if (hooks is null || hooks.Count == 0)
        {
            return new HookRunResult(Array.Empty<HookExecution>());
        }

        var executions = new List<HookExecution>();
        foreach (var hook in hooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timeout = hook.TimeoutSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : context.DefaultTimeout;

            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CRONBACKUP_JOB"] = context.JobName,
                ["CRONBACKUP_SOURCE"] = context.SourcePath,
                ["CRONBACKUP_DRY_RUN"] = context.DryRun ? "true" : "false",
            };

            if (context.ArchivePath is not null)
            {
                environment["CRONBACKUP_ARCHIVE"] = context.ArchivePath;
            }

            if (hook.Env is not null)
            {
                foreach (var item in hook.Env)
                {
                    environment[item.Key] = item.Value;
                }
            }

            ProcessResult result;
            try
            {
                result = await _processRunner.RunAsync(
                    new ProcessSpec(
                        hook.Run,
                        hook.Args ?? [],
                        hook.Cwd,
                        environment,
                        Timeout: timeout),
                    cancellationToken);
            }
            catch (ProcessExecutionException exception)
            {
                _logger.LogError(
                    exception,
                    "hook failed to start: phase={Phase} job={Job} hook={Hook}",
                    phase,
                    context.JobName,
                    hook.Run);
                executions.Add(new HookExecution(hook, -1, false, string.Empty, exception.Message));
                if (stopOnFailure)
                {
                    break;
                }

                continue;
            }

            var execution = new HookExecution(
                hook,
                result.ExitCode,
                result.TimedOut,
                result.StandardOutput,
                result.StandardError);
            executions.Add(execution);

            if (!execution.Succeeded)
            {
                _logger.LogWarning(
                    "hook failed: phase={Phase} job={Job} hook={Hook} exit={ExitCode} timedOut={TimedOut}",
                    phase,
                    context.JobName,
                    hook.Run,
                    result.ExitCode,
                    result.TimedOut);

                if (stopOnFailure)
                {
                    break;
                }
            }
        }

        return new HookRunResult(executions);
    }
}
