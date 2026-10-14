using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PlanForge.Orchestration;

/// <summary>Where a host exports its logs, as its profile says.</summary>
/// <param name="Host">The endpoint's host: <c>127.0.0.1</c>, <c>localhost</c> or <c>[::1]</c>.</param>
/// <param name="Port">The endpoint's port, where the collector is found or started.</param>
/// <param name="Interval">The host's export interval.</param>
internal sealed record ExportEndpoint(string Host, int Port, TimeSpan Interval);

/// <summary>What a profile says about the telemetry export.</summary>
/// <param name="Endpoint">The export's endpoint, or null when the profile does not configure it as R6 requires.</param>
/// <param name="Reason">Why, in key names only — never a value from the file.</param>
internal sealed record ExportVerdict(ExportEndpoint? Endpoint, string Reason);

/// <summary>
/// Reads the two hosts' profiles for R6's telemetry export: Claude Code's <c>settings.json</c> and
/// Codex's <c>config.toml</c>, given as text. Only the spellings R6 admits configure the export;
/// anything else reads as not configured.
/// </summary>
internal static class ExportProfile
{
    private const string LOGS_PATH = "/v1/logs";
    private const int DEFAULT_INTERVAL_MS = 5000;
    private const int MAX_INTERVAL_MS = 10_000;

    private static readonly string[] LOOPBACK_HOSTS = ["127.0.0.1", "localhost", "[::1]"];

    /// <summary>Claude Code's <c>settings.json</c>: the <c>env</c> object's export keys, string values only.</summary>
    /// <param name="settings">The file's text, or null when it is absent or unreadable.</param>
    public static ExportVerdict Claude(string? settings)
    {
        if (settings is null) return NotConfigured("settings.json is absent or unreadable");

        Dictionary<string, string> env;
        try
        {
            using var document = JsonDocument.Parse(settings);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("env", out var envObject) || envObject.ValueKind != JsonValueKind.Object)
            {
                return NotConfigured("settings.json has no env object");
            }

            // A value that is not a string counts as absent.
            env = envObject.EnumerateObject()
                           .Where(property => property.Value.ValueKind == JsonValueKind.String)
                           .GroupBy(property => property.Name, StringComparer.Ordinal)
                           .ToDictionary(group => group.Key, group => group.Last().Value.GetString()!, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return NotConfigured("settings.json does not parse as JSON");
        }

        string? Value(string name) => env.TryGetValue(name, out var value) ? value : null;

        if (Value("CLAUDE_CODE_ENABLE_TELEMETRY") is not { } enabled || !IsEnabled(enabled))
            return NotConfigured("CLAUDE_CODE_ENABLE_TELEMETRY is not 1 or true");
        if (Value("OTEL_LOGS_EXPORTER") is not { } exporters || !exporters.Split(',').Any(exporter => exporter.Trim() == "otlp"))
            return NotConfigured("OTEL_LOGS_EXPORTER does not list otlp");
        if ((Value("OTEL_EXPORTER_OTLP_LOGS_PROTOCOL") ?? Value("OTEL_EXPORTER_OTLP_PROTOCOL")) != "http/json")
            return NotConfigured("the OTLP logs protocol is not http/json");

        var endpoint = Value("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT")
                       ?? (Value("OTEL_EXPORTER_OTLP_ENDPOINT") is { } generic
                               ? (generic.EndsWith('/') ? generic[..^1] : generic) + LOGS_PATH
                               : null);
        if (endpoint is null) return NotConfigured("no OTLP logs endpoint is set");

        return Verdict(endpoint, Interval(Value("OTEL_LOGS_EXPORT_INTERVAL")), "settings.json");
    }

    /// <summary>
    /// Codex's <c>config.toml</c>: the <c>[otel]</c> exporter as <c>otlp-http</c> with <c>protocol = "json"</c>,
    /// spelled as an <c>exporter = { otlp-http = { … } }</c> line under <c>[otel]</c> or as an
    /// <c>[otel.exporter.otlp-http]</c> table.
    /// </summary>
    /// <param name="config">The file's text, or null when it is absent or unreadable.</param>
    public static ExportVerdict Codex(string? config)
    {
        if (config is null) return NotConfigured("config.toml is absent or unreadable");

        var entries = new TomlReader(config).Entries();
        if (entries.Any(entry => entry.Value is SpreadInlineTable
                                 && (entry.Table is ["otel"] && entry.Key is ["exporter", ..] || entry.Table is ["otel", "exporter", ..])))
        {
            return NotConfigured("config.toml spreads an inline table of the [otel] exporter over several lines");
        }

        IReadOnlyList<TomlEntry>? exporter = null;
        foreach (var entry in entries)
        {
            if (entry.Table is ["otel"] && entry.Key is ["exporter"] && entry.Value is IReadOnlyList<TomlEntry> outer
                && outer.LastOrDefault(inner => inner.Key is ["otlp-http"])?.Value is IReadOnlyList<TomlEntry> inline)
            {
                exporter = inline;
            }
        }

        exporter ??= entries.Where(entry => entry.Table is ["otel", "exporter", "otlp-http"]).ToList() is { Count: > 0 } table ? table : null;
        if (exporter is null) return NotConfigured("config.toml sets no [otel] otlp-http exporter in an admitted spelling");

        string? Text(string name) => exporter.LastOrDefault(entry => entry.Key.Length == 1 && entry.Key[0] == name)?.Value as string;

        if (Text("protocol") != "json") return NotConfigured("the otlp-http exporter's protocol is not json");
        if (Text("endpoint") is not { } endpoint) return NotConfigured("the otlp-http exporter has no endpoint");
        return Verdict(endpoint, TimeSpan.FromMilliseconds(DEFAULT_INTERVAL_MS), "config.toml");
    }

    /// <summary>
    /// Claude's export interval: a positive integer of milliseconds, however many digits, capped at
    /// 10 000; 5000 when absent, not an integer, or not positive.
    /// </summary>
    /// <param name="value">The <c>OTEL_LOGS_EXPORT_INTERVAL</c> value, if any.</param>
    internal static TimeSpan Interval(string? value)
    {
        if (value is not { Length: > 0 } || !value.All(char.IsAsciiDigit) || value.All(digit => digit == '0'))
            return TimeSpan.FromMilliseconds(DEFAULT_INTERVAL_MS);

        // Digits past what an int holds are still a positive integer, and so take the cap.
        return TimeSpan.FromMilliseconds(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
                                             ? Math.Min(ms, MAX_INTERVAL_MS)
                                             : MAX_INTERVAL_MS);
    }

    private static bool IsEnabled(string value) => value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>An endpoint R6 admits: an <c>http</c> URL on a loopback host whose path is exactly <c>/v1/logs</c>.</summary>
    /// <param name="endpoint">The logs endpoint.</param>
    /// <param name="interval">The export interval.</param>
    /// <param name="file">The profile file, for the reason.</param>
    private static ExportVerdict Verdict(string endpoint, TimeSpan interval, string file)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
            return NotConfigured($"the logs endpoint in {file} is not an http URL");
        if (!LOOPBACK_HOSTS.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            return NotConfigured($"the logs endpoint in {file} is not on 127.0.0.1, localhost or [::1]");
        if (uri.AbsolutePath != LOGS_PATH || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return NotConfigured($"the logs endpoint in {file} does not have the path {LOGS_PATH}");

        return new ExportVerdict(new ExportEndpoint(uri.Host, uri.Port, interval), $"{file} exports logs as OTLP/JSON to a loopback endpoint");
    }

    private static ExportVerdict NotConfigured(string reason) => new(null, reason);

    /// <summary>One key and value of a TOML file, under the table it sits in.</summary>
    /// <param name="Table">The table's dotted path; empty at the top.</param>
    /// <param name="Key">The key's dotted parts; a dotted key has more than one.</param>
    /// <param name="Value">A quoted string, an inline table as a list of entries, an array, a <see cref="BareScalar"/>, or <see cref="SpreadInlineTable"/>.</param>
    private sealed record TomlEntry(string[] Table, string[] Key, object Value);

    /// <summary>
    /// An unquoted value — a number, a boolean, a date, or text TOML would refuse — kept apart from a
    /// string, so that <c>endpoint = http://…</c> or <c>protocol = json</c> never reads as configured (R6).
    /// </summary>
    /// <param name="Text">The value as written.</param>
    private sealed record BareScalar(string Text);

    /// <summary>
    /// The value of a key whose inline table is spread over several lines: a layout R6 does not admit,
    /// which leaves the exporter it belongs to not configured.
    /// </summary>
    private sealed class SpreadInlineTable
    {
        public static readonly SpreadInlineTable Instance = new();
    }

    /// <summary>
    /// Just enough TOML to find R6's two spellings: tables, key/value lines with bare or double-quoted
    /// keys, double- and single-quoted strings, inline tables on one line, arrays, and comments. A line it
    /// cannot read — a multi-line string, an inline table spread over lines, anything else — is skipped,
    /// so its key never configures anything; an inline table spread over lines is skipped to its closing
    /// brace and its key kept as <see cref="SpreadInlineTable"/>.
    /// </summary>
    /// <param name="text">The file's text.</param>
    private sealed class TomlReader(string text)
    {
        private int _at;
        private int _inlineDepth;

        public List<TomlEntry> Entries()
        {
            var entries = new List<TomlEntry>();
            string[] table = [];
            while (_at < text.Length)
            {
                SkipBlank();
                if (_at >= text.Length) break;
                if (Peek() is '\r' or '\n' || Peek() == '#')
                {
                    SkipLine();
                    continue;
                }

                string[]? key = null;
                _inlineDepth = 0;
                try
                {
                    if (Peek() == '[')
                    {
                        // An array of tables never holds the exporter.
                        var array = text.AsSpan(_at).StartsWith("[[");
                        _at += array ? 2 : 1;
                        var path = DottedKey();
                        SkipBlank();
                        Expect(']');
                        if (array) Expect(']');
                        EndOfLine();
                        table = array ? ["[["] : path;
                    }
                    else
                    {
                        key = DottedKey();
                        SkipBlank();
                        Expect('=');
                        SkipBlank();
                        var value = Value(inline: false);
                        EndOfLine();
                        entries.Add(new TomlEntry(table, key, value));
                    }
                }
                catch (FormatException)
                {
                    // An inline table left open passes over whole, so none of its lines is read as a key
                    // of the table around it; one spread over lines is marked, so it configures nothing.
                    if (_inlineDepth > 0 && SkipInlineRest() && key is not null) entries.Add(new TomlEntry(table, key, SpreadInlineTable.Instance));
                    else SkipLine();
                }
            }

            return entries;
        }

        /// <summary>
        /// Passes over the rest of an inline table left open, to its closing brace and the end of that
        /// line, strings and comments on its lines included.
        /// </summary>
        /// <returns>Whether the inline table went on past a line break.</returns>
        private bool SkipInlineRest()
        {
            var depth = _inlineDepth;
            var spread = false;
            while (_at < text.Length && depth > 0)
            {
                var c = text[_at++];
                switch (c)
                {
                    case '\n':
                        spread = true;
                        break;
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        break;
                    case '#':
                        while (_at < text.Length && text[_at] != '\n') _at++;
                        break;
                    case '"' or '\'':
                        while (_at < text.Length && text[_at] != '\n' && text[_at] != c) _at += c == '"' && text[_at] == '\\' ? 2 : 1;
                        if (_at < text.Length && text[_at] == c) _at++;
                        break;
                }
            }

            SkipLine();
            return spread;
        }

        private char Peek() => _at < text.Length ? text[_at] : '\0';

        private void SkipBlank()
        {
            while (Peek() is ' ' or '\t') _at++;
        }

        private void SkipLine()
        {
            while (_at < text.Length && text[_at] != '\n') _at++;
            if (_at < text.Length) _at++;
        }

        private void EndOfLine()
        {
            SkipBlank();
            if (Peek() == '#') SkipLine();
            else if (Peek() == '\r' && _at + 1 < text.Length && text[_at + 1] == '\n') _at += 2;
            else if (Peek() == '\n') _at++;
            else if (_at < text.Length) throw new FormatException();
        }

        private void Expect(char c)
        {
            if (Peek() != c) throw new FormatException();
            _at++;
        }

        private string[] DottedKey()
        {
            var parts = new List<string>();
            while (true)
            {
                SkipBlank();
                parts.Add(KeyPart());
                SkipBlank();
                if (Peek() != '.') return [.. parts];
                _at++;
            }
        }

        /// <summary>A bare or double-quoted key; a single-quoted key is not admitted.</summary>
        private string KeyPart()
        {
            if (Peek() == '"') return BasicString();
            var start = _at;
            while (char.IsAsciiLetterOrDigit(Peek()) || Peek() is '_' or '-') _at++;
            return _at > start ? text[start.._at] : throw new FormatException();
        }

        /// <summary>A value; a line break is whitespace only inside an array.</summary>
        /// <param name="inline">Whether the value sits inside an inline table, where nothing may span lines.</param>
        private object Value(bool inline)
        {
            switch (Peek())
            {
                case '"':
                    return BasicString();
                case '\'':
                    return LiteralString();
                case '{':
                    return InlineTable();
                case '[':
                    return Array(inline);
                default:
                    var start = _at;
                    while (_at < text.Length && Peek() is not (' ' or '\t' or '\r' or '\n' or ',' or ']' or '}' or '#')) _at++;
                    return _at > start ? new BareScalar(text[start.._at]) : throw new FormatException();
            }
        }

        private List<TomlEntry> InlineTable()
        {
            Expect('{');
            _inlineDepth++;
            var entries = new List<TomlEntry>();
            SkipBlank();
            if (Peek() == '}')
            {
                _at++;
                _inlineDepth--;
                return entries;
            }

            while (true)
            {
                var key = DottedKey();
                SkipBlank();
                Expect('=');
                SkipBlank();
                entries.Add(new TomlEntry([], key, Value(inline: true)));
                SkipBlank();
                if (Peek() == '}')
                {
                    _at++;
                    _inlineDepth--;
                    return entries;
                }

                Expect(',');
                SkipBlank();
            }
        }

        private List<object> Array(bool inline)
        {
            Expect('[');
            var items = new List<object>();
            while (true)
            {
                SkipArrayBlank(inline);
                if (Peek() == ']')
                {
                    _at++;
                    return items;
                }

                items.Add(Value(inline));
                SkipArrayBlank(inline);
                if (Peek() == ',') _at++;
                else if (Peek() != ']') throw new FormatException();
            }
        }

        private void SkipArrayBlank(bool inline)
        {
            while (true)
            {
                SkipBlank();
                if (!inline && Peek() == '#') while (_at < text.Length && text[_at] != '\n') _at++;
                if (!inline && Peek() is '\r' or '\n') _at++;
                else return;
            }
        }

        private string BasicString()
        {
            if (text.AsSpan(_at).StartsWith("\"\"\"")) MultiLine("\"\"\"");
            Expect('"');
            var value = new StringBuilder();
            while (true)
            {
                var c = Peek();
                if (c is '\0' or '\r' or '\n') throw new FormatException();
                _at++;
                if (c == '"') return value.ToString();
                if (c != '\\')
                {
                    value.Append(c);
                    continue;
                }

                var escape = Peek();
                _at++;
                switch (escape)
                {
                    case 'b': value.Append('\b'); break;
                    case 't': value.Append('\t'); break;
                    case 'n': value.Append('\n'); break;
                    case 'f': value.Append('\f'); break;
                    case 'r': value.Append('\r'); break;
                    case '"': value.Append('"'); break;
                    case '\\': value.Append('\\'); break;
                    case 'u': value.Append(Unicode(4)); break;
                    case 'U': value.Append(Unicode(8)); break;
                    default: throw new FormatException();
                }
            }
        }

        private string Unicode(int digits)
        {
            if (_at + digits > text.Length
                || !int.TryParse(text.AsSpan(_at, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
                || code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
            {
                throw new FormatException();
            }

            _at += digits;
            return char.ConvertFromUtf32(code);
        }

        private string LiteralString()
        {
            if (text.AsSpan(_at).StartsWith("'''")) MultiLine("'''");
            Expect('\'');
            var start = _at;
            while (Peek() is not ('\'' or '\0' or '\r' or '\n')) _at++;
            if (Peek() != '\'') throw new FormatException();
            return text[start.._at++];
        }

        /// <summary>A multi-line string is not admitted: it is passed over whole, so its lines are not read as keys, and its line is refused.</summary>
        /// <param name="delimiter">The opening and closing delimiter.</param>
        private void MultiLine(string delimiter)
        {
            var end = text.IndexOf(delimiter, _at + delimiter.Length, StringComparison.Ordinal);
            _at = end < 0 ? text.Length : end + delimiter.Length;
            while (Peek() == delimiter[0]) _at++;
            throw new FormatException();
        }
    }
}
