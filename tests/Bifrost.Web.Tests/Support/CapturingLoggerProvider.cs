using Microsoft.Extensions.Logging;

namespace Bifrost.Web.Tests.Support;

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _lines = [];
    private readonly List<string> _warnings = [];

    /// <summary>Lines logged at Warning or above, to pin that an operator-facing notice reaches the journal.</summary>
    public IReadOnlyList<string> WarningLines
    {
        get
        {
            lock (_lines)
            {
                return _warnings.ToArray();
            }
        }
    }

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (owner._lines)
            {
                var line = formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception);
                owner._lines.Add(line);
                if (logLevel >= LogLevel.Warning)
                {
                    owner._warnings.Add(line);
                }
            }
        }
    }
}
