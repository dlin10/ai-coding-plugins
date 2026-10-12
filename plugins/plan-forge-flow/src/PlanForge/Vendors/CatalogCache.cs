using System.Collections.Concurrent;

namespace PlanForge.Vendors;

/// <summary>
/// One probe per vendor per server process, started in the background by `forge.begin` so the
/// interview finds the catalogues already fetched by the time it asks about vendors. A successful
/// probe is cached for the life of the process; a failed or degraded one is not, because its cause —
/// a missing binary, a sign-in, a sandbox that cannot start a shell — is fixable mid-session and the
/// next `forge.models` call should see the fix.
/// </summary>
internal sealed class CatalogCache
{
    internal static readonly string[] KnownVendors = ["claude", "codex", "cursor"];

    // Generous: the slowest probe measured is cursor-agent at ~3s. This is the only bound on the
    // codex probe, whose app-server requests are limited solely by the caller's token.
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<string?, string, IVendor> _createVendor;
    private readonly ShellHealth _shellHealth;
    private readonly ConcurrentDictionary<string, Lazy<Task<VendorCatalogReport>>> _probes =
        new(StringComparer.Ordinal);

    public CatalogCache() : this(VendorFactory.Create) { }

    /// <param name="createVendor">Builds the vendor a probe asks, from its id and the workspace root.</param>
    /// <param name="shellHealth">What Workers have shown about their shells; the process-wide record when null.</param>
    internal CatalogCache(Func<string?, string, IVendor> createVendor, ShellHealth? shellHealth = null)
    {
        _createVendor = createVendor;
        _shellHealth = shellHealth ?? ShellHealth.Process;
    }

    /// <summary>Fire-and-forget: kicks off every vendor's probe without awaiting any of them.</summary>
    public void BeginProbing(string workspaceRoot)
    {
        foreach (var vendor in KnownVendors)
            _ = Probe(vendor, workspaceRoot).Value;
    }

    /// <summary>
    /// The vendor's probe result, with what a Worker's own commands have shown since standing in for
    /// what the probe said about its shell: a Worker's evidence is newer than any probe and comes
    /// from the real launch rather than from codex's record of one.
    /// </summary>
    /// <param name="vendorId">The vendor to report.</param>
    /// <param name="workspaceRoot">The workspace a fresh probe runs in.</param>
    /// <param name="ct">Stops waiting for the probe; the probe itself runs on.</param>
    public async Task<VendorCatalogReport> GetAsync(string vendorId, string workspaceRoot, CancellationToken ct)
    {
        var id = vendorId.Trim().ToLowerInvariant();
        var probe = Probe(id, workspaceRoot);
        var report = await probe.Value.WaitAsync(ct).ConfigureAwait(false);

        if (!report.Available || report.Degraded is not null)
            _probes.TryRemove(new KeyValuePair<string, Lazy<Task<VendorCatalogReport>>>(id, probe));

        return _shellHealth.Degraded(id) is { } observed ? report with { Degraded = observed } : report;
    }

    private Lazy<Task<VendorCatalogReport>> Probe(string vendorId, string workspaceRoot) =>
        _probes.GetOrAdd(vendorId, id => new Lazy<Task<VendorCatalogReport>>(() => ProbeAsync(id, workspaceRoot)));

    private async Task<VendorCatalogReport> ProbeAsync(string vendorId, string workspaceRoot)
    {
        var vendor = _createVendor(vendorId, workspaceRoot);
        using var timeout = new CancellationTokenSource(_probeTimeout);
        var readiness = await vendor.ProbeAsync(timeout.Token).ConfigureAwait(false);
        return new VendorCatalogReport(vendor.Id, readiness.Available, readiness.Detail, vendor.Catalog, readiness.Degraded);
    }
}

/// <summary>One vendor's probe result as <c>forge.models</c> serves it.</summary>
/// <param name="Vendor">The vendor id.</param>
/// <param name="Available">Whether the vendor can do the work at all.</param>
/// <param name="Detail">The catalogue's size and any warnings, or why the vendor is unavailable.</param>
/// <param name="Catalog">The models the vendor advertises.</param>
/// <param name="Degraded">Why the vendor's Critic and Scout cannot start a shell, or null; see docs/adr/0031.</param>
internal sealed record VendorCatalogReport(string Vendor,
                                           bool Available,
                                           string Detail,
                                           VendorCatalog Catalog,
                                           string? Degraded = null);
