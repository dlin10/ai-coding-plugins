using System.Text.Json;
using PlanForge.Infrastructure;

namespace PlanForge.Vendors.Codex;

/// <summary>
/// Codex reached through `codex exec` rather than the App Server — see
/// docs/adr/0012-reach-codex-through-exec.md. The probe also has to rule out the Store-alias shell
/// failure before it ever asks codex to run a command — see
/// docs/adr/0013-strip-the-store-alias-from-the-codex-path.md.
/// </summary>
internal sealed class CodexCliVendor : IVendor
{
    private static readonly TimeSpan PROBE_TIMEOUT = TimeSpan.FromSeconds(30);

    private readonly string? _workingDirectory;

    public CodexCliVendor(string? workingDirectory = null)
    {
        _workingDirectory = workingDirectory;
        Catalog = new VendorCatalog([], CatalogSource.Live);
    }

    public string Id => "codex";

    /// <summary>Filled by <see cref="ProbeAsync"/> — Codex publishes a live model list.</summary>
    public VendorCatalog Catalog { get; private set; }

    /// <summary>
    /// A repair can itself fail, so the probe checks locally that the shell codex would choose is a
    /// real executable rather than an alias stub — a local filesystem check, spending no turn.
    /// </summary>
    internal static VendorReadiness? ShellReadiness(string? path)
    {
        if (CodexLaunch.Inspect(path).Shell is not null) return null;

        return new VendorReadiness(false,
            "codex cannot start a shell: the PowerShell on PATH is a Microsoft Store alias and no " +
            "other PowerShell was found — install PowerShell 7 to repair it");
    }

    public async Task<VendorReadiness> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var command = CodexLaunch.Command;

            var path = Environment.GetEnvironmentVariable("PATH");
            if (ShellReadiness(path) is { } shellReadiness) return shellReadiness;

            var inspected = CodexLaunch.Inspect(path);
            var environment = inspected.Repaired
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = inspected.Path! }
                : null;

            var spec = command.CreateProcess(["doctor", "--json"], _workingDirectory, string.Empty, environment);
            var lines = new List<string>();
            VendorException? exitFailure = null;
            try
            {
                await foreach (var line in StreamingProcess.RunAsync(spec, PROBE_TIMEOUT, ct).ConfigureAwait(false))
                    lines.Add(line);
            }
            catch (VendorException error) when (error.ExitCode is not null)
            {
                // doctor exits non-zero when any check fails, including checks a worker never
                // depends on, so the report it already printed decides rather than the exit code.
                exitFailure = error;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(string.Join('\n', lines));
            }
            catch (JsonException) when (exitFailure is not null)
            {
                return new VendorReadiness(false, exitFailure.Message);
            }

            string? warning;
            using (document)
            {
                (var refusal, warning) = JudgeDoctor(document.RootElement);
                if (refusal is not null) return refusal;
            }

            var modelsSpec = command.CreateProcess(["debug", "models"], _workingDirectory, string.Empty, environment);
            var modelLines = await StreamingProcess.CollectAsync(modelsSpec, PROBE_TIMEOUT, ct).ConfigureAwait(false);

            using var modelsDocument = JsonDocument.Parse(string.Join('\n', modelLines));
            Catalog = new VendorCatalog(ParseModels(modelsDocument.RootElement), CatalogSource.Live);

            var detail = $"{Catalog.Models.Count} models";
            return new VendorReadiness(true, warning is null ? detail : $"{detail}; {warning}");
        }
        catch (Exception error) when (error is VendorException or JsonException or OperationCanceledException
                                            or KeyNotFoundException or InvalidOperationException)
        {
            // Belt and braces: CatalogCache.ProbeAsync does not catch, so an exception escaping here
            // would reach the interview instead of an unavailable vendor with a reason — exactly
            // what R3 forbids. KeyNotFoundException and InvalidOperationException are what a
            // JsonElement read throws when the shape drifts.
            return new VendorReadiness(false, error.Message);
        }
    }

    public async Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken, CancellationToken ct)
    {
        var servers = await WorkerTools.GrantAsync(Id, role, ListServersAsync, ct).ConfigureAwait(false);
        return new CodexCliSession(role, selection, _workingDirectory, resumeToken, servers);
    }

    /// <summary>
    /// The servers a worker started here would load: every config layer, the project's
    /// `.codex/config.toml` included, as codex resolves them from the worker's own directory.
    /// </summary>
    private async Task<IReadOnlyList<string>> ListServersAsync(CancellationToken ct)
    {
        var spec = CodexLaunch.Command.CreateProcess(["mcp", "list", "--json"], _workingDirectory, string.Empty);
        var lines = await StreamingProcess.CollectAsync(spec, PROBE_TIMEOUT, ct).ConfigureAwait(false);

        using var document = JsonDocument.Parse(string.Join('\n', lines));
        return ParseServerList(document.RootElement);
    }

    /// <summary>
    /// Judges `codex doctor --json` by the checks a worker cannot run without, measured against
    /// codex-cli 0.154.0 on 2026-09-17: that version reported `overallStatus: fail` and exited 1
    /// for a failed `sandbox.helpers` alone, while `codex exec --sandbox read-only` answered.
    /// Sign-in must be `ok`; `installation` and `config.load` must not `fail`. Any other failed
    /// check is returned as a warning for the readiness detail instead of withholding codex.
    /// </summary>
    internal static (VendorReadiness? Refusal, string? Warning) JudgeDoctor(JsonElement root)
    {
        if (!root.TryGetProperty("checks", out var checks) || checks.ValueKind is not JsonValueKind.Object)
            return (Unrecognised(), null);

        if (!checks.TryGetProperty("auth.credentials", out var auth) || auth.ValueKind is not JsonValueKind.Object)
            return (Unrecognised(), null);

        if (OptionalString(auth, "status") is not { } authStatus)
            return (Unrecognised(), null);

        if (authStatus is not "ok")
            return (new VendorReadiness(false, OptionalString(auth, "summary") ?? "codex is not signed in"), null);

        var warnings = new List<string>();
        foreach (var check in checks.EnumerateObject())
        {
            if (check.Value.ValueKind is not JsonValueKind.Object || OptionalString(check.Value, "status") is not "fail")
                continue;

            var summary = OptionalString(check.Value, "summary");
            if (check.Name is "installation" or "config.load")
                return (new VendorReadiness(false, summary ?? $"codex doctor reports {check.Name} failed"), null);

            warnings.Add(summary is null ? $"{check.Name} failed" : $"{check.Name} failed: {summary}");
        }

        return (null, warnings.Count == 0 ? null : $"codex doctor warns: {string.Join("; ", warnings)}");
    }

    /// <summary>
    /// The enabled servers of `codex mcp list --json`, measured against codex-cli 0.154.0 on
    /// 2026-09-17. Only a name that is a bare TOML key is kept: the grant addresses the server
    /// through a dotted `-c` path, a name with a dot in it would address a different one, and a
    /// server codex cannot find there stops it before it starts.
    /// </summary>
    internal static List<string> ParseServerList(JsonElement root)
    {
        var servers = new List<string>();
        if (root.ValueKind is not JsonValueKind.Array) return servers;

        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind is not JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("name", out var name) || name.ValueKind is not JsonValueKind.String) continue;
            if (entry.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.False) continue;

            var value = name.GetString()!;
            if (value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
                servers.Add(value);
        }

        return servers;
    }

    /// <summary>
    /// Measured against `codex debug models` on 2026-09-04 to reproduce the App Server catalogue
    /// exactly. Only a missing `models` array throws, matching how the old client refused a page
    /// with no data — an entry missing a field of its own is skipped, not thrown on.
    /// </summary>
    internal static List<VendorModel> ParseModels(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var models) || models.ValueKind is not JsonValueKind.Array)
            throw new VendorException("codex debug models returned no models");

        var entries = new List<(string Slug, int Priority, JsonElement Entry)>();
        foreach (var entry in models.EnumerateArray())
        {
            if (entry.ValueKind is not JsonValueKind.Object) continue;

            if (!entry.TryGetProperty("visibility", out var visibility) || visibility.ValueKind is not JsonValueKind.String
                || visibility.GetString() is not "list")
                continue;

            if (!entry.TryGetProperty("slug", out var slug) || slug.ValueKind is not JsonValueKind.String
                || slug.GetString() is not { Length: > 0 } slugValue)
                continue;

            if (!entry.TryGetProperty("priority", out var priority) || priority.ValueKind is not JsonValueKind.Number
                || !priority.TryGetInt32(out var priorityValue))
                continue;

            entries.Add((slugValue, priorityValue, entry));
        }

        return [.. entries.OrderBy(item => item.Priority)
                          .Select((item, index) =>
                          {
                              var efforts = Efforts(item.Entry);
                              var fast = OffersFast(item.Entry);
                              return new VendorModel(item.Slug,
                                                     efforts,
                                                     DisplayName: OptionalString(item.Entry, "display_name"),
                                                     Description: OptionalString(item.Entry, "description"),
                                                     DefaultEffort: OptionalString(item.Entry, "default_reasoning_level"),
                                                     IsDefault: index == 0)
                              {
                                  FastEfforts = fast ? efforts : [],
                                  FastHint = fast ? FastHint(item.Entry) : null
                              };
                          })];
    }

    /// <summary>
    /// `service_tier="fast"` asks for the tier `additional_speed_tiers` lists as `fast`; codex does
    /// not validate the value, so a model without it would silently run at standard speed.
    /// </summary>
    private static bool OffersFast(JsonElement entry) =>
        entry.TryGetProperty("additional_speed_tiers", out var tiers) && tiers.ValueKind is JsonValueKind.Array
        && tiers.EnumerateArray().Any(tier => tier.ValueKind is JsonValueKind.String && tier.GetString() is "fast");

    /// <summary>The description of the tier codex names "Fast": "2x speed, increased usage" and the like.</summary>
    private static string? FastHint(JsonElement entry) =>
        entry.TryGetProperty("service_tiers", out var tiers) && tiers.ValueKind is JsonValueKind.Array
            ? tiers.EnumerateArray()
                   .Where(tier => tier.ValueKind is JsonValueKind.Object && OptionalString(tier, "name") is "Fast")
                   .Select(tier => OptionalString(tier, "description"))
                   .FirstOrDefault()
            : null;

    private static string[] Efforts(JsonElement entry) =>
        entry.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind is JsonValueKind.Array
            ? [.. levels.EnumerateArray()
                        .Select(level => level.ValueKind is JsonValueKind.Object
                                          && level.TryGetProperty("effort", out var effort)
                                          && effort.ValueKind is JsonValueKind.String
                            ? effort.GetString()
                            : null)
                        .OfType<string>()]
            : [];

    private static string? OptionalString(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static VendorReadiness Unrecognised() =>
        new(false, "codex answered doctor --json in a shape this version does not recognise");
}
