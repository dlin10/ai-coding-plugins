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
            var executable = CodexLaunch.Executable;

            var path = Environment.GetEnvironmentVariable("PATH");
            if (ShellReadiness(path) is { } shellReadiness) return shellReadiness;

            var inspected = CodexLaunch.Inspect(path);
            var environment = inspected.Repaired
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = inspected.Path! }
                : null;

            var spec = new ProcessSpec(executable, ["doctor", "--json"], _workingDirectory, string.Empty, environment);
            var lines = await StreamingProcess.CollectAsync(spec, PROBE_TIMEOUT, ct).ConfigureAwait(false);

            using var document = JsonDocument.Parse(string.Join('\n', lines));
            var root = document.RootElement;

            if (!root.TryGetProperty("checks", out var checks) || checks.ValueKind is not JsonValueKind.Object)
                return Unrecognised();

            if (!checks.TryGetProperty("auth.credentials", out var auth) || auth.ValueKind is not JsonValueKind.Object)
                return Unrecognised();

            if (!auth.TryGetProperty("status", out var status) || status.ValueKind is not JsonValueKind.String)
                return Unrecognised();

            if (status.GetString() is not "ok")
            {
                var summary = auth.TryGetProperty("summary", out var summaryValue) && summaryValue.ValueKind is JsonValueKind.String
                    ? summaryValue.GetString()
                    : null;

                return new VendorReadiness(false, summary ?? "codex is not signed in");
            }

            var modelsSpec = new ProcessSpec(executable, ["debug", "models"], _workingDirectory, string.Empty, environment);
            var modelLines = await StreamingProcess.CollectAsync(modelsSpec, PROBE_TIMEOUT, ct).ConfigureAwait(false);

            using var modelsDocument = JsonDocument.Parse(string.Join('\n', modelLines));
            Catalog = new VendorCatalog(ParseModels(modelsDocument.RootElement), CatalogSource.Live);

            return new VendorReadiness(true, $"{Catalog.Models.Count} models");
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

    public Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken, CancellationToken ct) =>
        Task.FromResult<IVendorSession>(new CodexCliSession(role, selection, _workingDirectory, resumeToken));

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
                          .Select((item, index) => new VendorModel(
                              item.Slug,
                              Efforts(item.Entry),
                              DisplayName: OptionalString(item.Entry, "display_name"),
                              Description: OptionalString(item.Entry, "description"),
                              DefaultEffort: OptionalString(item.Entry, "default_reasoning_level"),
                              IsDefault: index == 0))];
    }

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
