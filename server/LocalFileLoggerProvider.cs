using System.Text;
using Microsoft.Extensions.Logging;

namespace TheFlag.Server;

public sealed class LocalFileLoggerProvider : ILoggerProvider
{
    private readonly string _logPath;
    private readonly object _sync = new();

    public LocalFileLoggerProvider(string logPath)
    {
        _logPath = logPath;
        var directory = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new LocalFileLogger(categoryName, _logPath, _sync);
    }

    public void Dispose()
    {
    }

    private sealed class LocalFileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly string _logPath;
        private readonly object _sync;

        public LocalFileLogger(string categoryName, string logPath, object sync)
        {
            _categoryName = categoryName;
            _logPath = logPath;
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

            lock (_sync)
            {
                File.AppendAllText(_logPath, sb.ToString());
            }
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
