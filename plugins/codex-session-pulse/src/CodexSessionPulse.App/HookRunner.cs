using System.IO;
using System.Text.Json;

namespace CodexSessionPulse.App;

/// <summary>
/// The <c>--hook</c> path. It reads the hook payload from stdin, keeps it only in memory and never
/// writes it anywhere, reads nothing from the current directory, and always succeeds.
/// </summary>
internal static class HookRunner
{
    /// <summary>The maximum number of stdin bytes that are read.</summary>
    internal const int MaxPayloadBytes = 1024 * 1024;

    /// <summary>The environment variable that makes the hook print the names of the loaded assemblies.</summary>
    internal const string DebugAssembliesVariable = "CODEX_SESSION_PULSE_DEBUG_ASSEMBLIES";

    /// <summary>Reads the hook payload and returns the exit code.</summary>
    /// <param name="stdin">The stream holding the payload.</param>
    /// <param name="getStdout">Opens stdout; called only when the debug variable is set, so the normal path never creates the console writer.</param>
    /// <param name="getEnvironmentVariable">Reads an environment variable.</param>
    /// <returns>Always 0: a hook must never fail Codex.</returns>
    internal static int Run(Stream stdin, Func<TextWriter> getStdout, Func<string, string?> getEnvironmentVariable)
    {
        try
        {
            ReadPayload(stdin);
        }
        catch (Exception)
        {
            // A closed or broken stdin, or anything unexpected, must not fail Codex or raise a WER dialog.
            // Nothing from stdin is logged.
        }

        if (getEnvironmentVariable(DebugAssembliesVariable) == "1")
        {
            TextWriter stdout = getStdout();

            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                stdout.WriteLine(assembly.GetName().Name);
            }
        }

        return 0;
    }

    /// <summary>Reads at most <see cref="MaxPayloadBytes"/> bytes from the stream and parses them leniently.</summary>
    /// <param name="stdin">The stream holding the payload.</param>
    /// <returns>The payload; truncated or invalid JSON yields a payload with no fields.</returns>
    internal static HookPayload ReadPayload(Stream stdin)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];

        while (buffer.Length < MaxPayloadBytes)
        {
            int read = stdin.Read(chunk, 0, (int)Math.Min(chunk.Length, MaxPayloadBytes - buffer.Length));

            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            JsonElement root = document.RootElement;

            return new HookPayload(GetString(root, "hook_event_name"), GetString(root, "session_id"));
        }
        catch (JsonException)
        {
            return new HookPayload(null, null);
        }
    }

    /// <summary>Reads a string property of a JSON object, if it is one.</summary>
    /// <param name="element">The element to read from.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The string value, or <see langword="null"/> when the element is not an object or the property is not a string.</returns>
    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }
}

/// <summary>The fields of a hook payload that the hook path reads.</summary>
/// <param name="HookEventName">The lifecycle event name, when present.</param>
/// <param name="SessionId">The session id, when present.</param>
internal sealed record HookPayload(string? HookEventName, string? SessionId);
