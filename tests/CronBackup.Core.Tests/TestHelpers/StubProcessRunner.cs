using CronBackup.Core.Execution;

namespace CronBackup.Core.Tests.TestHelpers;

public sealed class StubProcessRunner : IProcessRunner
{
    private readonly Func<ProcessSpec, ProcessResult> _handler;

    public StubProcessRunner(Func<ProcessSpec, ProcessResult> handler)
    {
        _handler = handler;
    }

    public List<ProcessSpec> Specs { get; } = new();

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        Specs.Add(spec);
        return Task.FromResult(_handler(spec));
    }
}
