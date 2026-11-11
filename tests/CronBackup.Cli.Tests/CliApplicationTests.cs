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
        Assert.Contains("path=/backups", text, StringComparison.Ordinal);
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
    public async Task RunOnce_ShouldBackupToLocalDestination()
    {
        using var temp = new TestTempDirectory();
        var source = Path.Combine(temp.Path, "source");
        var destination = Path.Combine(temp.Path, "remote");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "data.txt"), "data");
        var jobName = $"photos-{Guid.NewGuid():N}";
        var config = $"""
            version: 1
            jobs:
              - name: {jobName}
                source:
                  path: "{source.Replace('\\', '/')}"
                destinations:
                  - name: local
                    kind: local
                    path: "{destination.Replace('\\', '/')}"
            """;
        var path = temp.WriteFile("config.yaml", config);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["run", "--once", "--config", path, "--log-level", "error"],
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("success", output.ToString(), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Combine(destination, "daily"), "*.tar.gz"));
        Assert.DoesNotContain("error:", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Prune_ShouldDefaultToDryRunAndApplyWhenRequested()
    {
        using var temp = new TestTempDirectory();
        var source = Path.Combine(temp.Path, "source");
        var destination = Path.Combine(temp.Path, "remote");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(destination, "daily"));
        var oldFile = Path.Combine(destination, "daily", "backup_old.tar.gz");
        var newFile = Path.Combine(destination, "daily", "backup_new.tar.gz");
        await File.WriteAllTextAsync(oldFile, "old");
        await File.WriteAllTextAsync(newFile, "new");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow.AddDays(-1));
        var jobName = $"prune-{Guid.NewGuid():N}";
        var config = $"""
            version: 1
            defaults:
              retention:
                daily: 1
                max_delete: 10
            jobs:
              - name: {jobName}
                source:
                  path: "{source.Replace('\\', '/')}"
                destinations:
                  - name: local
                    kind: local
                    path: "{destination.Replace('\\', '/')}"
            """;
        var path = temp.WriteFile("config.yaml", config);

        var dryRunOutput = new StringWriter();
        var dryRunExit = await CliApplication.RunAsync(
            ["prune", "--config", path],
            dryRunOutput,
            new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, dryRunExit);
        Assert.True(File.Exists(oldFile));
        Assert.Contains("backup_old.tar.gz", dryRunOutput.ToString(), StringComparison.Ordinal);

        var applyOutput = new StringWriter();
        var applyExit = await CliApplication.RunAsync(
            ["prune", "--config", path, "--apply"],
            applyOutput,
            new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, applyExit);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
    }

    [Fact]
    public async Task Restore_ShouldDownloadAndExtractLatestArchive()
    {
        using var temp = new TestTempDirectory();
        var source = Path.Combine(temp.Path, "source");
        var destination = Path.Combine(temp.Path, "remote");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "data.txt"), "restored data");
        var jobName = $"restore-{Guid.NewGuid():N}";
        var config = $"""
            version: 1
            jobs:
              - name: {jobName}
                source:
                  path: "{source.Replace('\\', '/')}"
                destinations:
                  - name: local
                    kind: local
                    path: "{destination.Replace('\\', '/')}"
            """;
        var path = temp.WriteFile("config.yaml", config);

        var runExit = await CliApplication.RunAsync(
            ["run", "--once", "--config", path, "--log-level", "error"],
            new StringWriter(),
            new StringWriter(),
            CancellationToken.None);
        Assert.Equal(0, runExit);

        var restoreDirectory = Path.Combine(temp.Path, "restore");
        var restoreOutput = new StringWriter();
        var restoreExit = await CliApplication.RunAsync(
            ["restore", "--job", jobName, "--latest", "--to", restoreDirectory, "--extract", "--config", path],
            restoreOutput,
            new StringWriter(),
            CancellationToken.None);

        Assert.Equal(0, restoreExit);
        Assert.Equal("restored data", await File.ReadAllTextAsync(Path.Combine(restoreDirectory, "data.txt")));
        Assert.Contains("restored", restoreOutput.ToString(), StringComparison.Ordinal);
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
    public void Parse_ShouldSupportEqualsSyntax()
    {
        var options = CliOptions.Parse(["plan", "--config=config.yaml", "--job=photos", "--tier=weekly"]);

        Assert.Equal("config.yaml", options.ConfigPath);
        Assert.Equal("photos", options.Job);
        Assert.Equal("weekly", options.Tier);
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
