using CronBackup.Cli.Logging;
using Microsoft.Extensions.Logging;

namespace CronBackup.Cli.Tests;

public sealed class FileLoggerProviderTests
{
    [Fact]
    public void Provider_ShouldRotateLogFile()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "backup.log");
        using var provider = new FileLoggerProvider(path, LogLevel.Information, maxBytes: 100, maxFiles: 2);
        var logger = provider.CreateLogger("test");

        logger.LogInformation(new string('a', 200));
        logger.LogInformation("second line");

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
    }
}
