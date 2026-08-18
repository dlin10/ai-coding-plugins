namespace PlanForge.Vendors;

/// <summary>One conversation with a vendor, in its own process.</summary>
internal interface IVendorSession : IAsyncDisposable
{
    /// <summary>Progress as the vendor produces it. Deliberately unread in production — keep it.</summary>
    /// <remarks>
    /// This is the seam for MCP progress. No host consumes it usefully yet: the Tasks extension is
    /// negotiated by nobody, and plain notifications/progress were measured on 2026-08-18 and
    /// rejected too — Cursor's client either sends no progressToken or never resets its clock on
    /// progress, and Claude Code needs only the manifest's per-server timeout. Both measurements
    /// sit in CONTEXT.md beside docs/adr/0002. Until a host moves, progress is visible only at the
    /// granularity of a tool call plus <c>forge.status</c>. Every session already emits here,
    /// which makes that day an adapter rather than a change to all three vendors.
    /// <para>
    /// A call-site search makes this look dead: the only readers are the integration tests, which
    /// count <see cref="VendorEventKind.Started"/> to hold the schema-retry budget. It is not dead,
    /// it is waiting.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<VendorEvent> Events { get; }

    /// <summary>Structure is mandatory: a vendor must return a valid object or fail.</summary>
    Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct);

    /// <summary>Only the Builder resumes; the Critic is always stateless.</summary>
    bool CanResume { get; }

    /// <summary>Token to hand back to StartAsync to continue this conversation, once known.</summary>
    string? ResumeToken { get; }
}
