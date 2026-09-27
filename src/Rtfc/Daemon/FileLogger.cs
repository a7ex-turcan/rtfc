using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Rtfc.Daemon;

/// <summary>
/// Appends one line per event to <c>rtfcd.log</c>. The daemon runs detached with its
/// stdio closed, so this is the only place its logs can go. Deliberately tiny: no
/// rotation yet, no structured output, no dependency.
/// </summary>
public sealed class FileLoggerProvider(string path, LogLevel minimum) : ILoggerProvider
{
    private readonly Lock _lock = new();
    private readonly LogLevel _minimum = minimum;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never take the daemon down.
            }
        }
    }

    public void Dispose()
    {
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var shortCategory = category[(category.LastIndexOf('.') + 1)..];
            var line = $"{DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {Level(logLevel)} {shortCategory}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Write(line);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none",
        };
    }
}
