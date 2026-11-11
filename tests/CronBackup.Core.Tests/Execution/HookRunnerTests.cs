using CronBackup.Core.Configuration;
using CronBackup.Core.Execution;
using CronBackup.Core.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CronBackup.Core.Tests.Execution;

public sealed class HookRunnerTests
{
    [Fact]
    public async Task RunAsync_ShouldSetContextEnvironment()
    {
        var processRunner = new StubProcessRunner(_ => new ProcessResult(0, "ok", "", false));
        var runner = new HookRunner(processRunner, NullLogger<HookRunner>.Instance);
        var hooks = new[]
        {
            new HookDefinition
            {
                Run = "/bin/echo",
                Args = ["hello"],
                Env = new Dictionary<string, string> { ["EXTRA"] = "value" },
            },
        };

        var result = await runner.RunAsync(
            HookPhase.Pre,
            hooks,
            new HookContext("photos", "/data", "/tmp/archive.tar.gz", false, TimeSpan.FromSeconds(5)),
            true,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var spec = Assert.Single(processRunner.Specs);
        Assert.Equal("/bin/echo", spec.FileName);
        Assert.Equal(["hello"], spec.Arguments);
        Assert.Equal("photos", spec.Environment!["CRONBACKUP_JOB"]);
        Assert.Equal("/data", spec.Environment["CRONBACKUP_SOURCE"]);
        Assert.Equal("/tmp/archive.tar.gz", spec.Environment["CRONBACKUP_ARCHIVE"]);
        Assert.Equal("value", spec.Environment["EXTRA"]);
        Assert.Equal(TimeSpan.FromSeconds(5), spec.Timeout);
    }

    [Fact]
    public async Task RunAsync_ShouldStopPreHooksOnFirstFailure()
    {
        var processRunner = new StubProcessRunner(spec =>
            spec.Arguments.Contains("fail")
                ? new ProcessResult(1, "", "failed", false)
                : new ProcessResult(0, "", "", false));
        var runner = new HookRunner(processRunner, NullLogger<HookRunner>.Instance);
        var hooks = new[]
        {
            new HookDefinition { Run = "/bin/check", Args = ["fail"] },
            new HookDefinition { Run = "/bin/check", Args = ["ok"] },
        };

        var result = await runner.RunAsync(
            HookPhase.Pre,
            hooks,
            new HookContext("photos", "/data", null, false, TimeSpan.FromSeconds(5)),
            stopOnFailure: true,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Single(result.Executions);
    }

    [Fact]
    public async Task RunAsync_ShouldRunAllPostHooksEvenWhenOneFails()
    {
        var processRunner = new StubProcessRunner(spec =>
            spec.Arguments.Contains("fail")
                ? new ProcessResult(1, "", "failed", false)
                : new ProcessResult(0, "", "", false));
        var runner = new HookRunner(processRunner, NullLogger<HookRunner>.Instance);
        var hooks = new[]
        {
            new HookDefinition { Run = "/bin/check", Args = ["fail"] },
            new HookDefinition { Run = "/bin/check", Args = ["ok"] },
        };

        var result = await runner.RunAsync(
            HookPhase.Post,
            hooks,
            new HookContext("photos", "/data", null, false, TimeSpan.FromSeconds(5)),
            stopOnFailure: false,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Executions.Count);
    }

    [Fact]
    public async Task RunAsync_ShouldTreatTimeoutAsFailure()
    {
        var processRunner = new StubProcessRunner(_ => new ProcessResult(-1, "", "", true));
        var runner = new HookRunner(processRunner, NullLogger<HookRunner>.Instance);
        var hooks = new[] { new HookDefinition { Run = "/bin/slow" } };

        var result = await runner.RunAsync(
            HookPhase.Pre,
            hooks,
            new HookContext("photos", "/data", null, false, TimeSpan.FromSeconds(1)),
            true,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.Executions[0].TimedOut);
    }
}
