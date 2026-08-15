namespace PlanForge.Vendors;

/// <summary>One conversation with a vendor, in its own process.</summary>
internal interface IVendorSession : IAsyncDisposable
{
    /// <summary>Progress as it happens. Observable at call granularity — no host streams it live.</summary>
    IAsyncEnumerable<VendorEvent> Events { get; }

    /// <summary>Structure is mandatory: a vendor must return a valid object or fail.</summary>
    Task<T> RunAsync<T>(string prompt, VendorSchema<T> schema, CancellationToken ct);

    /// <summary>Only the Builder resumes; the Critic is always stateless.</summary>
    bool CanResume { get; }

    /// <summary>Token to hand back to StartAsync to continue this conversation, once known.</summary>
    string? ResumeToken { get; }
}
