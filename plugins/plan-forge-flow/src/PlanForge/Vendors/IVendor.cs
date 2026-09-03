using System.Text.Json.Serialization.Metadata;

namespace PlanForge.Vendors;

/// <summary>A model supplier that can do work in a separate process.</summary>
internal interface IVendor
{
    string Id { get; }

    /// <summary>Models and effort levels this vendor advertises. Advisory: the vendor CLI decides.</summary>
    VendorCatalog Catalog { get; }

    Task<VendorReadiness> ProbeAsync(CancellationToken ct);

    /// <param name="selection"></param>
    /// <param name="resumeToken">
    /// A token from an earlier session of the same role. The MCP surface is stateless, so a
    /// Builder's continuity across separate tool calls has to be carried in the run state.
    /// </param>
    /// <param name="role"></param>
    /// <param name="ct"></param>
    Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken, CancellationToken ct);
}

internal enum VendorRole
{
    Critic,
    Builder
}

/// <param name="SystemPrompt">Role instructions, loaded from prompts/&lt;vendor&gt;/&lt;role&gt;.md.</param>
internal sealed record RoleSpec(VendorRole Role, string SystemPrompt);

/// <summary>
/// Model and effort are kept apart because vendors express effort differently — a flag for Claude,
/// a model property for Codex, part of the model string for Cursor. The vendor joins them.
/// </summary>
internal sealed record Selection(string Model, string? Effort);

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
                                   bool IsDefault = false);

internal enum VendorEventKind
{
    Started,
    Text,
    ToolUse,
    ToolResult,
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
