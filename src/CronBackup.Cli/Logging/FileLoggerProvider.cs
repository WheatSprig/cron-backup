using System.Text;
using Microsoft.Extensions.Logging;

namespace CronBackup.Cli.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly LogLevel _minimumLevel;
    private readonly StreamWriter _writer;

    public FileLoggerProvider(string path, LogLevel minimumLevel)
    {
        _path = path;
        _minimumLevel = minimumLevel;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(this, categoryName, _minimumLevel);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;
        private readonly LogLevel _minimumLevel;

        public FileLogger(FileLoggerProvider provider, string category, LogLevel minimumLevel)
        {
            _provider = provider;
            _category = category;
            _minimumLevel = minimumLevel;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var line = $"{DateTimeOffset.Now:O} [{logLevel}] {_category}: {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            _provider.Write(line);
        }
    }
}
