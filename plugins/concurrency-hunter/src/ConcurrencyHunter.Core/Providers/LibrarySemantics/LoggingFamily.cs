using static ConcurrencyHunter.Providers.LibrarySemantics.LibraryEffect;

namespace ConcurrencyHunter.Providers.LibrarySemantics;

/// <summary><c>Microsoft.Extensions.Logging.Abstractions</c> (R2): every logging extension reads each element of its <c>args</c>
/// deep, as the formatter would, and nothing else. <c>ILogger.Log&lt;TState&gt;</c> takes a formatter delegate and stays opaque;
/// configuration is not the table's.</summary>
internal static class LoggingFamily
{
    private const string LOGGER = "Microsoft.Extensions.Logging.ILogger";
    private const string EVENT_ID = "Microsoft.Extensions.Logging.EventId";

    private static readonly SupportedAssemblyVersion[] Assemblies =
        [SupportedAssemblyVersion.Framework("Microsoft.Extensions.Logging.Abstractions")];

    internal static IEnumerable<LibraryMember> Members =>
    [
        .. new[] { "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical" }.SelectMany(name => new[]
        {
            Log($"{name}({LOGGER},{EVENT_ID},System.Exception,System.String,System.Object[])"),
            Log($"{name}({LOGGER},{EVENT_ID},System.String,System.Object[])"),
            Log($"{name}({LOGGER},System.Exception,System.String,System.Object[])"),
            Log($"{name}({LOGGER},System.String,System.Object[])")
        }),
        Log($"Log({LOGGER},Microsoft.Extensions.Logging.LogLevel,System.String,System.Object[])"),
        Log($"Log({LOGGER},Microsoft.Extensions.Logging.LogLevel,{EVENT_ID},System.String,System.Object[])"),
        Log($"Log({LOGGER},Microsoft.Extensions.Logging.LogLevel,System.Exception,System.String,System.Object[])"),
        Log($"Log({LOGGER},Microsoft.Extensions.Logging.LogLevel,{EVENT_ID},System.Exception,System.String,System.Object[])"),
        Known($"M:{LOGGER}.IsEnabled(Microsoft.Extensions.Logging.LogLevel)~System.Boolean"),
        Known("M:Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger``1(Microsoft.Extensions.Logging.ILoggerFactory)~Microsoft.Extensions.Logging.ILogger{``0}"),
        Known($"M:Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger(Microsoft.Extensions.Logging.ILoggerFactory,System.Type)~{LOGGER}"),
        Known($"M:Microsoft.Extensions.Logging.ILoggerFactory.CreateLogger(System.String)~{LOGGER}"),
        Known($"M:{EVENT_ID}.#ctor(System.Int32,System.String)")
    ];

    private static LibraryMember Log(string signature) =>
        new($"M:Microsoft.Extensions.Logging.LoggerExtensions.{signature}", Assemblies, [DeepReadOf("args")]);

    private static LibraryMember Known(string id) => new(id, Assemblies, []);
}
