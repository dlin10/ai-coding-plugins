namespace PlanForgeFlow.Cli;

internal sealed class ParsedArgs
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

    private ParsedArgs() { }

    public IReadOnlyList<string> Positionals { get; private set; } = [];

    public bool Has(string name) => _values.ContainsKey(name);

    public IReadOnlyCollection<string> Names => _values.Keys;

    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public string GetRequired(string name)
    {
        var value = Get(name);
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "true", StringComparison.Ordinal))
        {
            throw new CliFailure("usage", $"--{name} is required");
        }

        return value;
    }

    public static ParsedArgs Parse(IEnumerable<string> args)
    {
        var parsed = new ParsedArgs();
        var positionals = new List<string>();
        var values = parsed._values;
        var list = args.ToArray();
        for (var index = 0; index < list.Length; index++)
        {
            var raw = list[index];
            if (!raw.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(raw);
                continue;
            }

            var token = raw[2..];
            var equals = token.IndexOf('=');
            if (equals >= 0)
            {
                if (values.ContainsKey(token[..equals])) throw new CliFailure("usage", $"option --{token[..equals]} was specified more than once");
                values[token[..equals]] = token[(equals + 1)..];
                continue;
            }

            if (index + 1 < list.Length && !list[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                if (values.ContainsKey(token)) throw new CliFailure("usage", $"option --{token} was specified more than once");
                values[token] = list[++index];
            }
            else
            {
                if (values.ContainsKey(token)) throw new CliFailure("usage", $"option --{token} was specified more than once");
                values[token] = "true";
            }
        }

        parsed.Positionals = positionals;
        return parsed;
    }
}