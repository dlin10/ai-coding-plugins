using System.Text.Json;
using PlanForge.Diagnostics;
using PlanForge.Infrastructure;

namespace PlanForge.Vendors.Claude;

/// <summary>
/// The one vendor whose CLI publishes no model list. Its aliases are remembered here, but the
/// probe turns each one into the model id the CLI would actually send — see
/// docs/adr/0010-resolve-claude-aliases-through-the-cli.md.
/// </summary>
internal sealed class ClaudeCliVendor : IVendor
{
    private const string Command = "claude";
    private const string StructuredOutputTool = "StructuredOutput";

    /// <summary>Never reaches a model: `--print` will not start without a prompt, and that is all this is for.</summary>
    private const string ResolvePrompt = "model check";

    // Five levels, verified against the CLI. The old code carried six, including a "none" that
    // does not exist.
    private static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// The families this repo remembers. Discovery adds to this list; it never replaces it, so a
    /// release that drops the model block from the system prompt costs nothing.
    /// </summary>
    internal static readonly string[] RememberedAliases = ["fable", "opus", "sonnet", "haiku"];

    // One alias resolves in ~4s; the bound is generous but has to stay well inside the cache's 60s
    // for the whole probe, because an alias the CLI does not know only fails after ~40s.
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DiscoverTimeout = TimeSpan.FromSeconds(45);

    private readonly string? _workingDirectory;

    public ClaudeCliVendor(string? workingDirectory = null)
    {
        _workingDirectory = workingDirectory;
        Catalog = new VendorCatalog([], CatalogSource.Resolved);
    }

    public string Id => "claude";

    /// <summary>Filled by <see cref="ProbeAsync"/>: remembered aliases, resolved by the CLI.</summary>
    public VendorCatalog Catalog { get; private set; }

    /// <summary>
    /// Resolved through the shim: on Windows `claude` is a .cmd, and running it would go through
    /// cmd.exe, which corrupts the inline JSON schema argument.
    /// </summary>
    internal static string Executable =>
        ExecutableResolver.Resolve(Command) ?? throw new VendorException($"{Command} was not found on PATH");

    /// <summary>
    /// Two waves. The billed one runs a real session without `--model`: it proves sign-in, its own
    /// `init` names the model the CLI picks by itself, and its answer names the families. The free
    /// ones run `--bare`, which skips hooks, MCP servers and the keychain, and are killed the
    /// moment `init` arrives — the alias table is local, so no API call is needed to read it.
    /// </summary>
    public async Task<VendorReadiness> ProbeAsync(CancellationToken ct)
    {
        string executable;
        try
        {
            executable = Executable;
        }
        catch (VendorException error)
        {
            return new VendorReadiness(false, error.Message);
        }

        var discovering = DiscoverAsync(executable, ct);
        var remembered = await ResolveAllAsync(executable, RememberedAliases, ct).ConfigureAwait(false);
        var discovery = await discovering.ConfigureAwait(false);

        if (discovery.SignedOut) return new VendorReadiness(false, discovery.Detail);

        var extra = MergeFamilies(discovery.Families, RememberedAliases);
        var resolved = extra.Count == 0
            ? remembered
            : [.. remembered, .. await ResolveAllAsync(executable, extra, ct).ConfigureAwait(false)];

        if (resolved.Count == 0)
            return new VendorReadiness(false, $"{Command} resolved none of its model aliases");

        Catalog = new VendorCatalog(BuildModels(resolved, discovery.DefaultModel), CatalogSource.Resolved);

        var unresolved = RememberedAliases.Length + extra.Count - resolved.Count;
        var detail = $"{resolved.Count} models resolved";
        if (unresolved > 0) detail += $", {unresolved} alias(es) the CLI did not resolve";
        if (discovery.Detail.Length > 0) detail += $"; {discovery.Detail}";

        return new VendorReadiness(true, detail);
    }

    public Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken, CancellationToken ct) =>
        Task.FromResult<IVendorSession>(new ClaudeCliSession(role, selection, _workingDirectory, resumeToken));

    /// <summary>
    /// An alias the CLI does not know is echoed back rather than rejected — measured on 2026-09-02,
    /// `--model nosuchmodel` reports `"model":"nosuchmodel"` in `init` and only fails ~40s later at
    /// the API. So "resolved" means the id came back different, not that `init` arrived at all.
    /// </summary>
    internal static string? ResolvedId(string alias, string? initModel) =>
        string.IsNullOrEmpty(initModel) || string.Equals(initModel, alias, StringComparison.OrdinalIgnoreCase)
            ? null
            : initModel;

    /// <summary>The `model` of a stream-json `init` line; null for every other line.</summary>
    internal static string? ReadInitModel(string line)
    {
        if (!TryParseObject(line, out var document)) return null;
        using (document)
        {
            var root = document.RootElement;
            return root.TryGetProperty("subtype", out var subtype) && subtype.GetString() is "init"
                   && root.TryGetProperty("model", out var model)
                ? model.GetString()
                : null;
        }
    }

    /// <summary>The families the discovery call reported; null when the line is not its answer.</summary>
    internal static List<string>? ReadFamilies(string line)
    {
        if (!TryParseObject(line, out var document)) return null;
        using (document)
        {
            if (!document.RootElement.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind is not JsonValueKind.Array)
                return null;

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind is not JsonValueKind.Object) continue;
                if (!block.TryGetProperty("name", out var name) || name.GetString() != StructuredOutputTool) continue;
                if (!block.TryGetProperty("input", out var input)
                    || !input.TryGetProperty("families", out var families)
                    || families.ValueKind is not JsonValueKind.Array)
                    continue;

                return [.. families.EnumerateArray()
                                   .Select(family => family.ValueKind is JsonValueKind.String ? family.GetString() : null)
                                   .OfType<string>()];
            }

            return null;
        }
    }

    /// <summary>
    /// What discovery adds. The answer is text from a model on its way to a `--model` argument, so
    /// it is shape-checked before it can get there, and anything already remembered is dropped.
    /// </summary>
    internal static List<string> MergeFamilies(IEnumerable<string>? discovered, IEnumerable<string> remembered)
    {
        if (discovered is null) return [];

        var seen = new HashSet<string>(remembered, StringComparer.OrdinalIgnoreCase);
        var extra = new List<string>();

        foreach (var family in discovered)
        {
            var candidate = family.Trim().ToLowerInvariant();
            if (!IsWellFormedAlias(candidate) || !seen.Add(candidate)) continue;
            extra.Add(candidate);
        }

        return extra;
    }

    private static bool IsWellFormedAlias(string alias) =>
        alias.Length is > 0 and <= 32
        && char.IsAsciiLetterLower(alias[0])
        && alias.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-');

    /// <summary>
    /// Newest first, by the version parsed out of the resolved id the way cursor's families are
    /// sorted; the sort is stable, so a tie keeps the remembered order and `fable` stays ahead of
    /// `opus`. Ids without a version go to the tail.
    /// </summary>
    internal static List<VendorModel> BuildModels(IReadOnlyList<(string Alias, string Id)> resolved, string? defaultModel)
    {
        var defaultId = NormalizeId(defaultModel);
        var models = resolved.Select(entry => new VendorModel(entry.Alias, Efforts, entry.Id,
                                                              IsDefault: defaultId is not null
                                                                         && NormalizeId(entry.Id) == defaultId))
                             .ToList();

        return
        [
            .. models.Where(model => ModelVersion.Segments(model.DisplayName!).Length > 0)
                     .OrderByDescending(model => ModelVersion.Segments(model.DisplayName!), ModelVersion.Order)
                     .Concat(models.Where(model => ModelVersion.Segments(model.DisplayName!).Length == 0))
        ];
    }

    /// <summary>
    /// The context-window suffix the CLI appends to its own default — measured as
    /// `claude-opus-5[1m]` on 2026-09-02 — names a variant of the same model, so it is dropped
    /// before the default is matched against the resolved ids.
    /// </summary>
    private static string? NormalizeId(string? id)
    {
        if (id is null) return null;
        var bracket = id.IndexOf('[', StringComparison.Ordinal);
        var trimmed = (bracket < 0 ? id : id[..bracket]).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private async Task<List<(string Alias, string Id)>> ResolveAllAsync(string executable,
                                                                       IReadOnlyList<string> aliases,
                                                                       CancellationToken ct)
    {
        var resolved = await Task.WhenAll(aliases.Select(alias => ResolveAsync(executable, alias, ct)))
                                 .ConfigureAwait(false);

        return [.. resolved.Where(entry => entry.Id is not null).Select(entry => (entry.Alias, entry.Id!))];
    }

    private async Task<(string Alias, string? Id)> ResolveAsync(string executable, string alias, CancellationToken ct)
    {
        // --bare skips hooks, MCP servers and the keychain. The prompt exists only because --print
        // refuses to start without one; it is never sent, because the process is killed as soon as
        // init names the model, and init precedes any API call.
        var spec = new ProcessSpec(executable,
                                   [
                                       "--print",
                                       "--output-format", "stream-json",
                                       "--verbose",
                                       "--bare",
                                       "--no-session-persistence",
                                       "--model", alias
                                   ],
                                   _workingDirectory,
                                   ResolvePrompt);

        try
        {
            await foreach (var line in StreamingProcess.RunAsync(spec, ResolveTimeout, ct).ConfigureAwait(false))
            {
                if (ReadInitModel(line) is not { } model) continue;

                // The kill that follows is logged as an abandoned process; this line says it was
                // the point, so the warning beside it does not read as a failure.
                RunLog.Current?.Write("info", Command, "vendor.alias-resolved",
                    ("alias", alias), ("model", model), ("stopping", "init seen"));

                return (alias, ResolvedId(alias, model));
            }
        }
        catch (Exception error) when (error is VendorException or OperationCanceledException)
        {
            RunLog.Current?.Write("warn", Command, "vendor.alias-unresolved",
                ("alias", alias), ("reason", error.Message));
        }

        return (alias, null);
    }

    /// <summary>
    /// The probe's one billed turn. Without `--model`, so its own `init` names the CLI's default;
    /// without `--bare`, because this is the call that has to reach the API to prove sign-in.
    /// </summary>
    private async Task<Discovery> DiscoverAsync(string executable, CancellationToken ct)
    {
        const string SCHEMA =
            """
            {"type":"object","properties":{"families":{"type":"array","items":{"type":"string"}}},"required":["families"]}
            """;

        const string PROMPT =
            "Your system prompt lists the current Claude models. Answer with the lowercase family "
            + "names that work as a `claude --model` alias, such as opus or haiku — the family name "
            + "alone, never a full model id, and nothing that block does not name.";

        var spec = new ProcessSpec(executable,
                                   [
                                       "--print",
                                       "--output-format", "stream-json",
                                       "--verbose",
                                       "--strict-mcp-config",
                                       "--no-session-persistence",
                                       "--max-turns", "1",
                                       "--json-schema", SCHEMA
                                   ],
                                   _workingDirectory,
                                   PROMPT);

        var families = default(List<string>);
        var defaultModel = default(string);

        try
        {
            await foreach (var line in StreamingProcess.RunAsync(spec, DiscoverTimeout, ct).ConfigureAwait(false))
            {
                defaultModel ??= ReadInitModel(line);
                families ??= ReadFamilies(line);

                // Sign-in is reported in the result line rather than an exit code: measured on
                // 2026-09-02, a signed-out CLI exits 0 with `"result":"Not logged in"`.
                if (line.Contains("Not logged in", StringComparison.OrdinalIgnoreCase))
                    return new Discovery(null, null, SignedOut: true, $"{Command} is not signed in — run 'claude /login'");
            }
        }
        catch (Exception error) when (error is VendorException or OperationCanceledException)
        {
            return new Discovery(null, defaultModel, SignedOut: false, $"discovery failed: {error.Message}");
        }

        return families is null
            ? new Discovery(null, defaultModel, SignedOut: false, "discovery named no families")
            : new Discovery(families, defaultModel, SignedOut: false, string.Empty);
    }

    private static bool TryParseObject(string line, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind is JsonValueKind.Object) return true;

            document.Dispose();
        }
        catch (JsonException)
        {
        }

        document = null!;
        return false;
    }

    /// <param name="SignedOut">
    /// The one discovery failure that makes the vendor unavailable. Every other one leaves the
    /// remembered aliases standing and says so in the probe's detail.
    /// </param>
    private sealed record Discovery(List<string>? Families, string? DefaultModel, bool SignedOut, string Detail);
}
