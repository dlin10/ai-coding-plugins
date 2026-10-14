using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlanForge.Vendors.Codex;

/// <summary>
/// What codex's Windows sandbox wrote down about its own setup, and how a command it could not start
/// comes back in the stream. Measured against codex-cli 0.162.1 on 2026-10-12: a sandboxed command
/// first runs a setup refresh, which logs to <c>CODEX_HOME/.sandbox/sandbox.&lt;yyyy-MM-dd&gt;.log</c>;
/// when the refresh fails, the command never starts. See CONTEXT.md and docs/adr/0031.
/// </summary>
internal static partial class CodexSandbox
{
    /// <summary>
    /// How codex reports a command it could not start, pinned by codex's own
    /// <c>unified_exec_launch_failure_tests.rs</c>: status <c>failed</c>, exit code -1, and output that
    /// opens with this text.
    /// </summary>
    internal const string LAUNCH_FAILURE = "Failed to create unified exec process:";

    private const string REFRESH_ERRORS = "setup refresh completed with errors: ";
    private const int QUOTE_LIMIT = 400;

    /// <summary>
    /// Whether a completed <c>command_execution</c> item is a command codex could not start, and if so
    /// what codex said instead of its output.
    /// </summary>
    /// <param name="item">A completed <c>command_execution</c> item.</param>
    /// <param name="diagnostic">Receives codex's launch diagnostic, on one line and bounded.</param>
    internal static bool IsLaunchFailure(JsonElement item, out string diagnostic)
    {
        diagnostic = string.Empty;
        if (item.ValueKind is not JsonValueKind.Object) return false;

        if (!item.TryGetProperty("status", out var status) || status.ValueKind is not JsonValueKind.String
            || status.GetString() is not "failed")
            return false;

        if (!item.TryGetProperty("exit_code", out var exitCode) || exitCode.ValueKind is not JsonValueKind.Number
            || !exitCode.TryGetInt32(out var code) || code != -1)
            return false;

        if (!item.TryGetProperty("aggregated_output", out var output) || output.ValueKind is not JsonValueKind.String
            || output.GetString() is not { } text || !text.StartsWith(LAUNCH_FAILURE, StringComparison.Ordinal))
            return false;

        diagnostic = Quote(text);
        return true;
    }

    /// <summary>
    /// The error code a launch diagnostic carries after its fixed opening —
    /// <c>helper_unknown_error</c> in "Failed to create unified exec process: helper_unknown_error:
    /// setup refresh had errors" — or null when what follows is not a bare identifier.
    /// </summary>
    /// <param name="diagnostic">A launch diagnostic, as <see cref="IsLaunchFailure"/> returned it.</param>
    internal static string? ErrorCode(string diagnostic)
    {
        if (!diagnostic.StartsWith(LAUNCH_FAILURE, StringComparison.Ordinal)) return null;

        var rest = diagnostic[LAUNCH_FAILURE.Length..].TrimStart();
        var end = rest.IndexOf(':');
        var candidate = end < 0 ? rest : rest[..end];
        return Identifier().IsMatch(candidate) ? candidate : null;
    }

    /// <summary>
    /// The first error of the last sandbox setup refresh that failed, from the newest dated sandbox
    /// log, or null when there is none or it cannot be read. It names what blocked the setup — on
    /// 2026-10-12, the desktop app's <c>node_repl.exe</c> with "os error 32".
    /// </summary>
    /// <param name="codexHome">The codex home; <c>CODEX_HOME</c>, else <c>~/.codex</c>, when null.</param>
    internal static string? LastSetupError(string? codexHome = null)
    {
        try
        {
            var directory = Path.Combine(codexHome ?? Home(), ".sandbox");
            if (!Directory.Exists(directory)) return null;

            var newest = Directory.EnumerateFiles(directory, "sandbox.*.log")
                                  .Where(path => DatedLog().IsMatch(Path.GetFileName(path)))
                                  .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
                                  .FirstOrDefault();
            if (newest is null) return null;

            // codex may be appending while this reads, so the file is shared every way it allows.
            using var stream = new FileStream(newest, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            string? last = null;
            while (reader.ReadLine() is { } line)
            {
                if (SetupError(line) is { } error) last = error;
            }

            return last;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The first error a "setup refresh completed with errors" line lists, or null for any other line.
    /// The list is Rust's debug form of a string vector, which reads as JSON for everything measured;
    /// anything that does not is quoted as written.
    /// </summary>
    /// <param name="line">One line of a sandbox log.</param>
    internal static string? SetupError(string line)
    {
        var start = line.IndexOf(REFRESH_ERRORS, StringComparison.Ordinal);
        if (start < 0) return null;

        var list = line[(start + REFRESH_ERRORS.Length)..].Trim();
        try
        {
            using var document = JsonDocument.Parse(list);
            if (document.RootElement.ValueKind is JsonValueKind.Array
                && document.RootElement.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.String } first)
                return Quote(first.GetString()!);
        }
        catch (JsonException)
        {
        }

        return list.Length == 0 ? null : Quote(list);
    }

    /// <summary>Codex's home: <c>CODEX_HOME</c>, else <c>~/.codex</c>.</summary>
    /// <param name="environment">Reads an environment variable; the process's own when null.</param>
    internal static string Home(Func<string, string?>? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariable)("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>One line, bounded, so a quoted diagnostic cannot carry a whole command's output.</summary>
    private static string Quote(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= QUOTE_LIMIT ? line : line[..QUOTE_LIMIT] + "…";
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^sandbox\.\d{4}-\d{2}-\d{2}\.log$")]
    private static partial Regex DatedLog();
}
