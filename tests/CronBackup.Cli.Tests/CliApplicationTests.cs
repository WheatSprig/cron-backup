using CronBackup.Cli;

namespace CronBackup.Cli.Tests;

public sealed class CliApplicationTests
{
    private const string Config = """
        version: 1
        defaults:
          retention:
            daily: 7
            max_delete: 20
        jobs:
          - name: photos
            schedule: "0 2 * * *"
            source:
              path: /backup/data
            destinations:
              - name: local
                kind: local
                path: /backups
        """;

    [Fact]
    public async Task Validate_ShouldReturnZero_ForValidConfig()
    {
        using var temp = new TestTempDirectory();
        var path = temp.WriteFile("config.yaml", Config);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["validate", "--config", path],
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("valid:", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Plan_ShouldPrintJobAndRetention()
    {
        using var temp = new TestTempDirectory();
        var path = temp.WriteFile("config.yaml", Config);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["plan", "--config", path],
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("job photos", text, StringComparison.Ordinal);
        Assert.Contains("daily=7", text, StringComparison.Ordinal);
        Assert.Contains("local", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Plan_ShouldEmitJson()
    {
        using var temp = new TestTempDirectory();
        var path = temp.WriteFile("config.yaml", Config);
        var output = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["plan", "--config", path, "--json"],
            output,
            new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"jobs\"", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"photos\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_ShouldReturnTwo_ForInvalidConfig()
    {
        using var temp = new TestTempDirectory();
        var path = temp.WriteFile("config.yaml", Config.Replace("kind: local", "kind: ftp"));
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["validate", "--config", path],
            new StringWriter(),
            error,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("kind", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ShouldReportNotImplemented()
    {
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["run"],
            new StringWriter(),
            error,
            CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Contains("not implemented", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownCommand_ShouldReturnOne()
    {
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["nope"],
            new StringWriter(),
            error,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown command", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Version_ShouldReturnZero()
    {
        var output = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["version"],
            output,
            new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("cron-backup", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ShouldSupportOptionsBeforeAndAfterCommand()
    {
        var options = CliOptions.Parse(["--config", "config.yaml", "plan", "--job", "photos", "--json"]);

        Assert.Equal("plan", options.Command);
        Assert.Equal("config.yaml", options.ConfigPath);
        Assert.Equal("photos", options.Job);
        Assert.True(options.Json);
    }

    [Fact]
    public void Parse_ShouldRejectUnknownOption()
    {
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["validate", "--wat"]));
    }
}

internal sealed class TestTempDirectory : IDisposable
{
    public TestTempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "cron-backup-cli-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string WriteFile(string relativePath, string content)
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
