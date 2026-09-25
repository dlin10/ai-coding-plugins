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
    private const string COMMAND = "claude";
    private const string STRUCTURED_OUTPUT_TOOL = "StructuredOutput";

    /// <summary>Never reaches a model: `--print` will not start without a prompt, and that is all this is for.</summary>
    private const string RESOLVE_PROMPT = "model check";

    /// <summary>The opt-in whose answer `init` reports as <c>fast_mode_state</c>.</summary>
    private const string FAST_OPT_IN = """{"fastMode":true}""";

    // Five levels, verified against the CLI. The old code carried six, including a "none" that
    // does not exist.
    private static readonly string[] EFFORTS = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// The families this repo remembers. Discovery adds to this list; it never replaces it, so a
    /// release that drops the model block from the system prompt costs nothing.
    /// </summary>
    internal static readonly string[] RememberedAliases = ["fable", "opus", "sonnet", "haiku"];

    // One alias resolves in ~4s; the bound is generous but has to stay well inside the cache's 60s
    // for the whole probe, because an alias the CLI does not know only fails after ~40s.
    private static readonly TimeSpan RESOLVE_TIMEOUT = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DISCOVER_TIMEOUT = TimeSpan.FromSeconds(45);

    // About 4 s measured with five servers refusing the connection; one that hangs costs its own
    // startup timeout, so the bound is wide rather than tight.
    private static readonly TimeSpan LIST_TIMEOUT = TimeSpan.FromSeconds(60);

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
        ExecutableResolver.Resolve(COMMAND) ?? throw new VendorException($"{COMMAND} was not found on PATH");

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

        // The account is asked once, through a model that offers Fast, while discovery still runs.
        var fastAlias = remembered.FirstOrDefault(entry => entry.Fast).Alias;
        var checkingAccount = fastAlias is null
            ? Task.FromResult<string?>(null)
            : AccountFastRefusalAsync(executable, fastAlias, ct);
        var discovery = await discovering.ConfigureAwait(false);

        if (discovery.SignedOut) return new VendorReadiness(false, discovery.Detail);

        var extra = MergeFamilies(discovery.Families, RememberedAliases);
        var resolved = extra.Count == 0
            ? remembered
            : [.. remembered, .. await ResolveAllAsync(executable, extra, ct).ConfigureAwait(false)];

        if (resolved.Count == 0)
            return new VendorReadiness(false, $"{COMMAND} resolved none of its model aliases");

        var fastAliases = resolved.Where(entry => entry.Fast).Select(entry => entry.Alias).ToHashSet(StringComparer.Ordinal);
        var accountRefusal = await checkingAccount.ConfigureAwait(false);
        if (fastAlias is null && fastAliases.FirstOrDefault() is { } discoveredFastAlias)
            accountRefusal = await AccountFastRefusalAsync(executable, discoveredFastAlias, ct).ConfigureAwait(false);

        var models = BuildModels([.. resolved.Select(entry => (entry.Alias, entry.Id))], discovery.DefaultModel,
                                 fastAliases, accountRefusal);
        Catalog = new VendorCatalog(models, CatalogSource.Resolved);

        var unresolved = RememberedAliases.Length + extra.Count - resolved.Count;
        var detail = $"{resolved.Count} models resolved";
        if (unresolved > 0) detail += $", {unresolved} alias(es) the CLI did not resolve";
        if (discovery.Detail.Length > 0) detail += $"; {discovery.Detail}";

        return new VendorReadiness(true, detail);
    }

    public async Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken, CancellationToken ct)
    {
        var servers = await WorkerTools.GrantAsync(Id, role, ListServersAsync, ct).ConfigureAwait(false);
        return new ClaudeCliSession(role, selection, _workingDirectory, resumeToken, servers);
    }

    /// <summary>
    /// The servers a worker started here would load. `claude mcp list` is the only listing the CLI
    /// has, prints text and health-checks every server on the way; this plugin's own server is told
    /// to exit at once through the marker Cursor workers already carry, rather than being started
    /// and handshaken for nothing.
    /// </summary>
    private async Task<IReadOnlyList<string>> ListServersAsync(CancellationToken ct)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Cursor.CursorAgentSession.SelfExclusionEnvironment] = "1"
        };
        var spec = new ProcessSpec(Executable, ["mcp", "list"], _workingDirectory, string.Empty, environment);
        return ParseServerList(await StreamingProcess.CollectAsync(spec, LIST_TIMEOUT, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// A server line reads `name: target - status`, and a plugin server's name holds colons of its
    /// own (`plugin:context7:context7`), so the name ends at the first colon followed by a space.
    /// Measured against Claude Code 2.1.273 on 2026-09-17; the header and blank lines have no such
    /// separator and are skipped.
    /// </summary>
    internal static List<string> ParseServerList(IEnumerable<string> lines)
    {
        var servers = new List<string>();
        foreach (var line in lines)
        {
            var end = line.IndexOf(": ", StringComparison.Ordinal);
            if (end <= 0 || line.IndexOf(" - ", end, StringComparison.Ordinal) < 0) continue;

            servers.Add(line[..end]);
        }

        return servers;
    }

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

    /// <summary>The fast state of a stream-json `init` line; null for every other line.</summary>
    internal static (string State, string? Reason)? ReadInitFast(string line)
    {
        if (!TryParseObject(line, out var document)) return null;
        using (document) return InitFast(document.RootElement);
    }

    /// <summary>
    /// `fast_mode_state` and, when it is off, `fast_mode_disabled_reason` of an `init` message: what
    /// the session will be served before any API call. Measured against Claude Code 2.1.282 on
    /// 2026-09-25 — `sdk_opt_in_required` without `"fastMode": true` in `--settings`,
    /// `extra_usage_disabled` for an account without overage, no reason at all for a model without
    /// Fast under `--bare`.
    /// </summary>
    internal static (string State, string? Reason)? InitFast(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object
            || !root.TryGetProperty("subtype", out var subtype) || subtype.ValueKind is not JsonValueKind.String
            || subtype.GetString() is not "init"
            || !root.TryGetProperty("fast_mode_state", out var state) || state.ValueKind is not JsonValueKind.String)
            return null;

        var reason = root.TryGetProperty("fast_mode_disabled_reason", out var why) && why.ValueKind is JsonValueKind.String
            ? why.GetString()
            : null;
        return (state.GetString()!, reason);
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
                if (!block.TryGetProperty("name", out var name) || name.GetString() != STRUCTURED_OUTPUT_TOOL) continue;
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
    internal static List<VendorModel> BuildModels(IReadOnlyList<(string Alias, string Id)> resolved,
                                                  string? defaultModel,
                                                  IReadOnlySet<string>? fastAliases = null,
                                                  string? fastUnavailable = null)
    {
        var defaultId = NormalizeId(defaultModel);
        var models = resolved.Select(entry =>
                                     {
                                         var fast = fastAliases?.Contains(entry.Alias) is true;
                                         return new VendorModel(entry.Alias, EFFORTS, entry.Id,
                                                                IsDefault: defaultId is not null && NormalizeId(entry.Id) == defaultId)
                                         {
                                             FastEfforts = fast ? EFFORTS : [],
                                             FastUnavailable = fast ? fastUnavailable : null
                                         };
                                     })
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

    private async Task<List<(string Alias, string Id, bool Fast)>> ResolveAllAsync(string executable,
                                                                                  IReadOnlyList<string> aliases,
                                                                                  CancellationToken ct)
    {
        var resolved = await Task.WhenAll(aliases.Select(alias => ResolveAsync(executable, alias, ct)))
                                 .ConfigureAwait(false);

        return [.. resolved.Where(entry => entry.Id is not null).Select(entry => (entry.Alias, entry.Id!, entry.Fast))];
    }

    private async Task<(string Alias, string? Id, bool Fast)> ResolveAsync(string executable, string alias, CancellationToken ct)
    {
        // --bare skips hooks, MCP servers and the keychain. The prompt exists only because --print
        // refuses to start without one; it is never sent, because the process is killed as soon as
        // init names the model, and init precedes any API call. The Fast opt-in rides along because
        // a session that cannot see the account judges the model alone.
        var spec = new ProcessSpec(executable,
                                   [
                                       "--print",
                                       "--output-format", "stream-json",
                                       "--verbose",
                                       "--bare",
                                       "--no-session-persistence",
                                       "--settings", FAST_OPT_IN,
                                       "--model", alias
                                   ],
                                   _workingDirectory,
                                   RESOLVE_PROMPT);

        try
        {
            await foreach (var line in StreamingProcess.RunAsync(spec, RESOLVE_TIMEOUT, ct).ConfigureAwait(false))
            {
                if (ReadInitModel(line) is not { } model) continue;

                var fast = ReadInitFast(line)?.State is "on";

                // The kill that follows is logged as an abandoned process; this line says it was
                // the point, so the warning beside it does not read as a failure.
                RunLog.Current?.Write("info", COMMAND, "vendor.alias-resolved",
                    ("alias", alias), ("model", model), ("fast", fast ? "true" : "false"), ("stopping", "init seen"));

                return (alias, ResolvedId(alias, model), fast);
            }
        }
        catch (Exception error) when (error is VendorException or OperationCanceledException)
        {
            RunLog.Current?.Write("warn", COMMAND, "vendor.alias-unresolved",
                ("alias", alias), ("reason", error.Message));
        }

        return (alias, null, false);
    }

    /// <summary>
    /// Why the account will not serve a Fast tier its models offer, or null when it will: one
    /// `init` with the account visible, killed before any API call like the resolve wave — about
    /// 3.4 s against 0.7 s under `--bare`, measured 2026-09-25. A cooldown is a moment rather than a
    /// refusal, so it is not held against a catalogue that lives as long as the server; a check that
    /// could not be made is, because a Fast request nothing confirmed is refused (docs/adr/0023).
    /// </summary>
    private async Task<string?> AccountFastRefusalAsync(string executable, string alias, CancellationToken ct)
    {
        var spec = new ProcessSpec(executable,
                                   [
                                       "--print",
                                       "--output-format", "stream-json",
                                       "--verbose",
                                       "--strict-mcp-config",
                                       "--no-session-persistence",
                                       "--settings", FAST_OPT_IN,
                                       "--model", alias
                                   ],
                                   _workingDirectory,
                                   RESOLVE_PROMPT);

        try
        {
            await foreach (var line in StreamingProcess.RunAsync(spec, RESOLVE_TIMEOUT, ct).ConfigureAwait(false))
            {
                if (ReadInitFast(line) is not { } fast) continue;

                RunLog.Current?.Write("info", COMMAND, "vendor.fast-checked",
                    ("alias", alias), ("state", fast.State), ("reason", fast.Reason), ("stopping", "init seen"));

                return fast.State is "off" ? fast.Reason ?? "off" : null;
            }
        }
        catch (Exception error) when (error is VendorException or OperationCanceledException)
        {
            RunLog.Current?.Write("warn", COMMAND, "vendor.fast-unchecked", ("alias", alias), ("reason", error.Message));
            return $"not confirmed: {error.Message}";
        }

        return "not confirmed: init reported no fast_mode_state";
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
            await foreach (var line in StreamingProcess.RunAsync(spec, DISCOVER_TIMEOUT, ct).ConfigureAwait(false))
            {
                defaultModel ??= ReadInitModel(line);
                families ??= ReadFamilies(line);

                // Sign-in is reported in the result line rather than an exit code: measured on
                // 2026-09-02, a signed-out CLI exits 0 with `"result":"Not logged in"`.
                if (line.Contains("Not logged in", StringComparison.OrdinalIgnoreCase))
                    return new Discovery(null, null, SignedOut: true, $"{COMMAND} is not signed in — run 'claude /login'");
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
