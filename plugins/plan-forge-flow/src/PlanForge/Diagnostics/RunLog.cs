using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanForge.Infrastructure;

namespace PlanForge.Diagnostics;

/// <summary>
/// The run's operational log: one JSON object per line in <c>.forge/&lt;runId&gt;/forge.log</c>.
/// </summary>
/// <remarks>
/// Distinct from <c>flow_log.md</c>, which records the results of acts that
/// succeeded. This one records what happened, especially when nothing succeeded — a failed act used
/// to leave the run folder holding <c>state.json</c> and nothing else.
/// <para>
/// JSONL rather than prose because the interesting fields are themselves multi-line — a vendor
/// command line, a stack trace, a tail of stderr — and one object per line keeps them greppable
/// without an escaping convention of our own.
/// </para>
/// <para>
/// Writes go through <see cref="AtomicFile.Append"/> for the same reason the other run files do:
/// stdio gives one server process per client, and a host may have the plugin registered globally
/// and per repository at once, so two processes can be appending to one run.
/// </para>
/// </remarks>
internal sealed class RunLog
{
    /// <summary>Long fields are cut rather than dropped; the head is what identifies the call.</summary>
    private const int MAX_FIELD_LENGTH = 2000;

    private const string ELISION = "… [truncated]";

    private static readonly AsyncLocal<RunLog?> AMBIENT = new();
    private static RunLog? _last;

    private readonly string _path;

    public RunLog(string path) => _path = path;

    /// <summary>
    /// The log of the run whose tool call is executing, or — outside a call — the last one this
    /// process served.
    /// </summary>
    /// <remarks>
    /// The fallback is what makes the MCP layer's own logging useful. Transport and dispatch
    /// entries can arrive on a context that never flowed through a tool handler, and a run id is
    /// the one thing they cannot carry; without the fallback those are exactly the entries a
    /// timeout would drop.
    /// </remarks>
    public static RunLog? Current => AMBIENT.Value ?? Volatile.Read(ref _last);

    /// <summary>Makes <paramref name="log"/> ambient for the current async flow, and nothing more.</summary>
    /// <remarks>
    /// Deliberately not published as the fallback: a log scoped here is reachable only through
    /// the ambient context, so writers on unrelated flows can never interleave their entries into
    /// it. That isolation is what lets a test assert on the file's exact contents while parallel
    /// tests spawn processes of their own. A tool call serving a run wants the opposite and goes
    /// through <see cref="Serve"/>.
    /// </remarks>
    public static IDisposable Use(RunLog log)
    {
        var previous = AMBIENT.Value;
        AMBIENT.Value = log;
        return new Scope(previous);
    }

    /// <summary>
    /// Makes <paramref name="log"/> ambient for the duration of one tool call, and publishes it
    /// as the last log this process served — the fallback <see cref="Current"/> hands to contexts
    /// that never flowed through a tool handler.
    /// </summary>
    public static IDisposable Serve(RunLog log)
    {
        Volatile.Write(ref _last, log);
        return Use(log);
    }

    public void Write(string level, string source, string name, params (string Name, string? Value)[] fields)
    {
        var carried = new Dictionary<string, string>(fields.Length, StringComparer.Ordinal);
        foreach (var (field, value) in fields)
        {
            if (value is null) continue;
            carried[field] = Truncate(value);
        }

        var entry = new LogEntry(DateTimeOffset.Now, level, source, name, carried.Count == 0 ? null : carried);

        // A run that cannot be diagnosed is bad; a run that fails *because* diagnosis failed is
        // worse. Nothing here may take the call down with it.
        try
        {
            AtomicFile.Append(_path, JsonSerializer.Serialize(entry, DiagnosticJson.Readable.LogEntry) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    public static string Truncate(string value) =>
        value.Length <= MAX_FIELD_LENGTH ? value : value[..MAX_FIELD_LENGTH] + ELISION;

    /// <summary>The tail, not the head: a process that dies says why in its last lines.</summary>
    public static string Tail(string value) =>
        value.Length <= MAX_FIELD_LENGTH ? value : ELISION + value[^MAX_FIELD_LENGTH..];

    private sealed class Scope(RunLog? previous) : IDisposable
    {
        public void Dispose() => AMBIENT.Value = previous;
    }
}

internal sealed record LogEntry(DateTimeOffset At,
                                string Level,
                                string Source,
                                string Event,
                                IReadOnlyDictionary<string, string>? Fields);

// Reflection-based serialization is off repo-wide, so this needs a source-generated contract too.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LogEntry))]
internal sealed partial class DiagnosticJson : JsonSerializerContext
{
    /// <summary>For the file a person opens: non-ASCII text as written, not <c>\uXXXX</c>.</summary>
    internal static DiagnosticJson Readable =>
        field ??= new(new JsonSerializerOptions(Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
