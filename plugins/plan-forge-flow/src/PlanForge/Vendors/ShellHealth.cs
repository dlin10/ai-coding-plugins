using System.Collections.Concurrent;

namespace PlanForge.Vendors;

/// <summary>
/// What this server process last learned from a sandboxed Worker's own commands — a Critic's or a
/// Scout's — about whether that vendor's sandbox can start a shell. <c>forge.models</c> reports a
/// failure as the vendor's <c>degraded</c>, beside what the probe found, because the probe runs once
/// per process and codex's own record of a failed sandbox setup is cleared by the next setup any
/// codex attempts, the Codex desktop app's included. See docs/adr/0031.
/// </summary>
/// <remarks>
/// A Builder neither records nor clears anything here: codex runs it outside the sandbox, so its
/// commands start while the sandbox setup fails, and its success says nothing about the roles that
/// read through the sandbox.
/// </remarks>
internal sealed class ShellHealth
{
    /// <summary>The instance every vendor session and the catalogue share in a server process.</summary>
    public static ShellHealth Process { get; } = new();

    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.Ordinal);

    /// <summary>Records that a sandboxed Worker of this vendor started none of the commands it tried.</summary>
    /// <param name="vendor">The vendor id.</param>
    /// <param name="diagnosis">What the Worker tried and what the vendor said, for the user to read.</param>
    public void Failed(string vendor, string diagnosis) => _failures[vendor] = diagnosis;

    /// <summary>Records that a sandboxed Worker of this vendor started a command, which ends an earlier failure.</summary>
    /// <param name="vendor">The vendor id.</param>
    public void Started(string vendor) => _failures.TryRemove(vendor, out _);

    /// <summary>Why this vendor's sandboxed Workers cannot start a shell, or null when nothing here says so.</summary>
    /// <param name="vendor">The vendor id.</param>
    public string? Degraded(string vendor) => _failures.TryGetValue(vendor, out var diagnosis) ? diagnosis : null;
}
