using System.Text;
using Microsoft.Extensions.Logging;

namespace CronBackup.Cli.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly LogLevel _minimumLevel;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private StreamWriter _writer;
    private long _currentBytes;

    public FileLoggerProvider(
        string path,
        LogLevel minimumLevel,
        long maxBytes = 50 * 1024 * 1024,
        int maxFiles = 3)
    {
        _path = path;
        _minimumLevel = minimumLevel;
        _maxBytes = Math.Max(1, maxBytes);
        _maxFiles = Math.Max(1, maxFiles);
        _writer = OpenWriter();
        _currentBytes = File.Exists(_path) ? new FileInfo(_path).Length : 0;
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
            RotateIfNeeded();
            _writer.WriteLine(line);
            _currentBytes += Encoding.UTF8.GetByteCount(line) +
                             Encoding.UTF8.GetByteCount(_writer.NewLine);
        }
    }

    private StreamWriter OpenWriter()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new StreamWriter(
            new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    private void RotateIfNeeded()
    {
        if (_currentBytes < _maxBytes)
        {
            return;
        }

        try
        {
            _writer.Dispose();

            if (_maxFiles == 1)
            {
                File.Delete(_path);
            }
            else
            {
                for (var index = _maxFiles - 1; index >= 1; index--)
                {
                    var source = index == 1 ? _path : $"{_path}.{index - 1}";
                    var target = $"{_path}.{index}";
                    if (File.Exists(source))
                    {
                        File.Move(source, target, overwrite: true);
                    }
                }
            }

            _writer = OpenWriter();
            _currentBytes = 0;
        }
        catch (IOException)
        {
            // 轮转失败时继续追加，至少不丢当前日志。
            _writer = OpenWriter();
            _currentBytes = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        }
        catch (UnauthorizedAccessException)
        {
            _writer = OpenWriter();
            _currentBytes = File.Exists(_path) ? new FileInfo(_path).Length : 0;
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
