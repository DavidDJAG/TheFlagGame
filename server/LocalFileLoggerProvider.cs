using System.Text;
using Microsoft.Extensions.Logging;

namespace TheFlag.Server;

public sealed class LocalFileLoggerProvider : ILoggerProvider
{
    private readonly string _logPath;
    private readonly long _maxFileBytes;
    private readonly int _retainedFileCount;
    private readonly object _sync = new();

    public LocalFileLoggerProvider(string logPath, long maxFileBytes = 5 * 1024 * 1024, int retainedFileCount = 5)
    {
        if (maxFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes), "The maximum log file size must be positive.");
        }

        if (retainedFileCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedFileCount), "At least one rotated log file must be retained.");
        }

        _logPath = logPath;
        _maxFileBytes = maxFileBytes;
        _retainedFileCount = retainedFileCount;

        var directory = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new LocalFileLogger(categoryName, _logPath, _maxFileBytes, _retainedFileCount, _sync);
    }

    public void Dispose()
    {
    }

    private sealed class LocalFileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly string _logPath;
        private readonly long _maxFileBytes;
        private readonly int _retainedFileCount;
        private readonly object _sync;

        public LocalFileLogger(string categoryName, string logPath, long maxFileBytes, int retainedFileCount, object sync)
        {
            _categoryName = categoryName;
            _logPath = logPath;
            _maxFileBytes = maxFileBytes;
            _retainedFileCount = retainedFileCount;
            _sync = sync;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Information;
        }

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
            if (string.IsNullOrWhiteSpace(message) && exception is null)
            {
                return;
            }

            var sb = new StringBuilder();
            sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
            sb.Append(" [");
            sb.Append(logLevel);
            sb.Append("] ");
            sb.Append(_categoryName);

            if (eventId.Id != 0 || !string.IsNullOrWhiteSpace(eventId.Name))
            {
                sb.Append(" (");
                sb.Append(eventId.Id);
                if (!string.IsNullOrWhiteSpace(eventId.Name))
                {
                    sb.Append(":");
                    sb.Append(eventId.Name);
                }
                sb.Append(")");
            }

            sb.Append(": ");
            sb.AppendLine(message);

            if (exception is not null)
            {
                sb.AppendLine(exception.ToString());
            }

            var entry = sb.ToString();
            lock (_sync)
            {
                RotateIfNeeded(entry);
                File.AppendAllText(_logPath, entry, Encoding.UTF8);
            }
        }

        private void RotateIfNeeded(string nextEntry)
        {
            var currentLength = File.Exists(_logPath) ? new FileInfo(_logPath).Length : 0L;
            var nextEntryBytes = Encoding.UTF8.GetByteCount(nextEntry);
            if (currentLength + nextEntryBytes <= _maxFileBytes)
            {
                return;
            }

            var oldestPath = GetArchivePath(_retainedFileCount);
            if (File.Exists(oldestPath))
            {
                File.Delete(oldestPath);
            }

            for (var i = _retainedFileCount - 1; i >= 1; i--)
            {
                var sourcePath = GetArchivePath(i);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                var destinationPath = GetArchivePath(i + 1);
                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }

                File.Move(sourcePath, destinationPath);
            }

            if (File.Exists(_logPath) && currentLength > 0L)
            {
                var firstArchivePath = GetArchivePath(1);
                if (File.Exists(firstArchivePath))
                {
                    File.Delete(firstArchivePath);
                }

                File.Move(_logPath, firstArchivePath);
            }
        }

        private string GetArchivePath(int index)
        {
            var directory = Path.GetDirectoryName(_logPath);
            var fileName = Path.GetFileNameWithoutExtension(_logPath);
            var extension = Path.GetExtension(_logPath);
            var archiveFileName = $"{fileName}.{index}{extension}";
            return string.IsNullOrWhiteSpace(directory)
                ? archiveFileName
                : Path.Combine(directory, archiveFileName);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
