using System.Text.Json.Serialization.Metadata;

namespace PlanForge.Vendors;

/// <summary>A model supplier that can do work in a separate process.</summary>
internal interface IVendor
{
    string Id { get; }

    /// <summary>Models and effort levels this vendor advertises. Advisory: the vendor CLI decides.</summary>
    VendorCatalog Catalog { get; }

    Task<VendorReadiness> ProbeAsync(CancellationToken ct);

    /// <summary>
    /// The selection in this vendor's canonical spelling, before anything records or confirms it:
    /// cursor reads a <c>-fast</c> written into the model or effort as a Fast request.
    /// </summary>
    Selection Normalize(Selection selection) => selection;

    /// <summary>
    /// Why the catalogue does not confirm this Fast request, or null when it does: the model is
    /// listed — by its id, or by the model id it resolved to — with Fast at the requested effort, and
    /// nothing says the account refuses it. Cursor confirms by joined id instead.
    /// </summary>
    string? RefuseFast(Selection selection, VendorCatalog catalog)
    {
        var model = catalog.Models.FirstOrDefault(entry =>
            string.Equals(entry.Id, selection.Model, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.DisplayName, selection.Model, StringComparison.OrdinalIgnoreCase));

        if (model is null) return "the catalogue does not list this model";
        if (model.FastEfforts.Count == 0) return "the catalogue offers no Fast tier for this model";
        if (model.FastUnavailable is { } reason) return $"the account will not serve its Fast tier ({reason})";

        return selection.Effort is null || model.FastEfforts.Contains(selection.Effort, StringComparer.OrdinalIgnoreCase)
            ? null
            : $"the catalogue offers no Fast tier at effort \"{selection.Effort}\"";
    }

    /// <param name="selection"></param>
    /// <param name="resumeToken">
    /// A token from an earlier session of the same role. The MCP surface is stateless, so a
    /// Builder's or Scout's continuity across separate tool calls has to be carried in run state.
    /// </param>
    /// <param name="role"></param>
    /// <param name="ct"></param>
    Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken, CancellationToken ct);
}

internal enum VendorRole
{
    Critic,
    Builder,
    Scout
}

/// <param name="SystemPrompt">Role instructions, loaded from prompts/&lt;vendor&gt;/&lt;role&gt;.md.</param>
/// <param name="WritableRoots">
/// Absolute paths outside the workspace a Builder may write to, from <c>forge.begin</c>. Carried
/// on the role because it is a fact about the builder's sandbox, and only codex has a sandbox to
/// tell; the other vendors ignore it.
/// </param>
/// <param name="WorkerTools">
/// Patterns naming the MCP servers this worker may call unasked, from <c>forge.begin</c>; null or
/// empty grants none. Each vendor matches them against its own server list at start — see
/// <see cref="Vendors.WorkerTools"/>.
/// </param>
internal sealed record RoleSpec(VendorRole Role,
                                string SystemPrompt,
                                IReadOnlyList<string>? WritableRoots = null,
                                IReadOnlyList<string>? WorkerTools = null,
                                WorkerTelemetryContext? Telemetry = null);

/// <summary>
/// Model and effort are kept apart because vendors express effort differently — a flag for Claude,
/// a model property for Codex, part of the model string for Cursor. The vendor joins them. Fast is
/// kept apart for the same reason — a settings key for Claude, a service tier for Codex, a suffix
/// for Cursor — and is asked for out loud either way; see docs/adr/0023.
/// </summary>
internal sealed record Selection(string Model, string? Effort, bool Fast = false);

internal sealed record VendorReadiness(bool Available, string Detail);

/// <param name="Source">Where the catalogue came from. The interview tells the user which kind it is offering.</param>
internal sealed record VendorCatalog(IReadOnlyList<VendorModel> Models, CatalogSource Source);

internal enum CatalogSource
{
    /// <summary>The vendor reported the list itself: codex, cursor.</summary>
    Live,

    /// <summary>
    /// The aliases are a list this repo remembers, but the vendor resolved each one into the model
    /// it stands for: claude. An alias it did not resolve is not in the catalogue.
    /// </summary>
    Resolved
}

/// <param name="DefaultEffort">The effort the vendor picks when none is asked for, where it says.</param>
/// <param name="IsDefault">True for the model the vendor itself would pick.</param>
internal sealed record VendorModel(string Id,
                                   IReadOnlyList<string> Efforts,
                                   string? DisplayName = null,
                                   string? Description = null,
                                   string? DefaultEffort = null,
                                   bool IsDefault = false)
{
    /// <summary>The efforts this model is offered at with a Fast tier; empty when it has none.</summary>
    public IReadOnlyList<string> FastEfforts { get; init; } = [];

    /// <summary>What the vendor says Fast costs, where it says: codex's tier description.</summary>
    public string? FastHint { get; init; }

    /// <summary>
    /// Why the account will not serve a Fast tier the model itself offers, where the vendor says:
    /// claude's <c>fast_mode_disabled_reason</c>, such as <c>extra_usage_disabled</c>.
    /// </summary>
    public string? FastUnavailable { get; init; }
}

internal enum VendorEventKind
{
    Started,
    Text,
    ToolUse,
    ToolResult,

    /// <summary>A task the vendor runs on the worker's behalf started, changed or ended.</summary>
    Task,
    Finished,
    Failed
}

/// <param name="Fields">
/// Structured detail — a command line, an exit code, an output tail — logged as separate JSONL
/// fields so the run log stays greppable. Null for events that are just their text.
/// </param>
internal sealed record VendorEvent(VendorEventKind Kind,
                                   string Text,
                                   IReadOnlyList<(string Name, string? Value)>? Fields = null);

/// <summary>
/// The wire schema plus its deserialization contract. Reflection-based serialization is disabled
/// repo-wide, so the contract must be source-generated rather than derived at runtime.
/// </summary>
internal sealed record VendorSchema<T>(string Json, JsonTypeInfo<T> TypeInfo);

internal sealed class VendorException(string message, int? exitCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Set when the failure is a nonzero exit; absent for a launch failure, timeout or output cap.</summary>
    public int? ExitCode { get; } = exitCode;
}
