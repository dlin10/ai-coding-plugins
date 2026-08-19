using System.Collections.Concurrent;

namespace PlanForge.Vendors;

/// <summary>
/// One probe per vendor per server process, started in the background by `forge.begin` so the
/// interview finds the catalogues already fetched by the time it asks about vendors. A successful
/// probe is cached for the life of the process; a failed one is not, because its cause — a missing
/// binary, a sign-in — is fixable mid-session and the next `forge.models` call should see the fix.
/// </summary>
internal sealed class CatalogCache
{
    internal static readonly string[] KnownVendors = ["claude", "codex", "cursor"];

    // Generous: the slowest probe measured is cursor-agent at ~3s. This is the only bound on the
    // codex probe, whose app-server requests are limited solely by the caller's token.
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<string?, string, IVendor> _createVendor;
    private readonly ConcurrentDictionary<string, Lazy<Task<VendorCatalogReport>>> _probes =
        new(StringComparer.Ordinal);

    public CatalogCache() : this(VendorFactory.Create) { }

    internal CatalogCache(Func<string?, string, IVendor> createVendor) => _createVendor = createVendor;

    /// <summary>Fire-and-forget: kicks off every vendor's probe without awaiting any of them.</summary>
    public void BeginProbing(string workspaceRoot)
    {
        foreach (var vendor in KnownVendors)
            _ = Probe(vendor, workspaceRoot).Value;
    }

    public async Task<VendorCatalogReport> GetAsync(string vendorId, string workspaceRoot, CancellationToken ct)
    {
        var id = vendorId.Trim().ToLowerInvariant();
        var probe = Probe(id, workspaceRoot);
        var report = await probe.Value.WaitAsync(ct).ConfigureAwait(false);

        if (!report.Available) _probes.TryRemove(new KeyValuePair<string, Lazy<Task<VendorCatalogReport>>>(id, probe));

        return report;
    }

    private Lazy<Task<VendorCatalogReport>> Probe(string vendorId, string workspaceRoot) =>
        _probes.GetOrAdd(vendorId, id => new Lazy<Task<VendorCatalogReport>>(() => ProbeAsync(id, workspaceRoot)));

    private async Task<VendorCatalogReport> ProbeAsync(string vendorId, string workspaceRoot)
    {
        var vendor = _createVendor(vendorId, workspaceRoot);
        using var timeout = new CancellationTokenSource(_probeTimeout);
        var readiness = await vendor.ProbeAsync(timeout.Token).ConfigureAwait(false);
        return new VendorCatalogReport(vendor.Id, readiness.Available, readiness.Detail, vendor.Catalog);
    }
}

internal sealed record VendorCatalogReport(string Vendor, bool Available, string Detail, VendorCatalog Catalog);
