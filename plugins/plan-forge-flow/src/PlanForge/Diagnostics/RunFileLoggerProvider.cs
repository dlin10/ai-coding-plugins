using Microsoft.Extensions.Logging;

namespace PlanForge.Diagnostics;

/// <summary>
/// Routes <see cref="ILogger"/> output into the current run's <c>forge.log</c>.
/// </summary>
/// <remarks>
/// The reason to bridge MEL at all rather than only call <see cref="RunLog"/> directly: the MCP SDK
/// logs its own dispatch and transport through <see cref="ILogger"/>, and until now
/// <c>ClearProviders</c> threw all of it away. A tool call that times out inside the SDK — the
/// failure that motivated this — leaves its trace here and nowhere else.
/// <para>
/// The file cannot be chosen at startup, because the run folder is derived from arguments that
/// arrive with each call; the provider therefore resolves <see cref="RunLog.Current"/> per entry.
/// Entries produced before the first run exists have nowhere to go and are dropped.
/// </para>
/// </remarks>
internal sealed class RunFileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RunFileLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class RunFileLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel,
                                EventId eventId,
                                TState state,
                                Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (RunLog.Current is not { } log) return;

            // "log" rather than anything more specific: the categories reaching here include the
            // hosting lifetime, and naming its shutdown message after MCP would be a lie in the
            // one field a reader scans first.
            log.Write(Level(logLevel), category, eventId.Name is { Length: > 0 } name ? name : "log",
                ("message", formatter(state, exception)),
                ("exception", exception?.ToString()));
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Critical or LogLevel.Error => "error",
            LogLevel.Warning => "warn",
            _ => "info"
        };
    }
}
