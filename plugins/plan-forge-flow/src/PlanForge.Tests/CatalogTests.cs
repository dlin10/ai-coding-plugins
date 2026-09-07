using System.Text.Json.Nodes;
using PlanForge.Mcp;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

public sealed class CatalogTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "planforge-catalog-" + Guid.NewGuid().ToString("N"));

    public CatalogTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Models_reports_every_vendor_with_its_source_and_availability()
    {
        var run = NewRun("models");
        var cache = new CatalogCache((id, _) => id switch
        {
            "claude" => new CatalogVendor("claude", available: true, new VendorCatalog([
                new VendorModel("opus", ["low", "high"], "claude-opus-5")
            ], CatalogSource.Resolved)),
            "codex" => new CatalogVendor("codex", available: true, new VendorCatalog([
                new VendorModel("gpt-5.6-sol", ["low", "ultra"], "GPT-5.6-Sol", "Latest.", "low", IsDefault: true)
            ], CatalogSource.Live)),
            _ => new CatalogVendor("cursor", available: false, new VendorCatalog([], CatalogSource.Live),
                detail: "cursor-agent was not found on PATH")
        });

        var result = JsonNode.Parse(await ForgeTools.Models(cache, SessionRoots.None, _workspace, run.RunId, CancellationToken.None))!;

        var vendors = result["vendors"]!.AsArray();
        Assert.Equal(["claude", "codex", "cursor"], vendors.Select(v => v!["vendor"]!.GetValue<string>()));
        Assert.Equal(["resolved", "live", "live"], vendors.Select(v => v!["source"]!.GetValue<string>()));
        Assert.Equal([true, true, false], vendors.Select(v => v!["available"]!.GetValue<bool>()));

        var sol = vendors[1]!["models"]!.AsArray().Single()!;
        Assert.Equal("gpt-5.6-sol", sol["id"]!.GetValue<string>());
        Assert.Equal("GPT-5.6-Sol", sol["displayName"]!.GetValue<string>());
        Assert.Equal(["low", "ultra"], sol["efforts"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Equal("low", sol["defaultEffort"]!.GetValue<string>());
        Assert.True(sol["isDefault"]!.GetValue<bool>());

        var cursor = vendors[2]!;
        Assert.Equal("cursor-agent was not found on PATH", cursor["detail"]!.GetValue<string>());
        Assert.Empty(cursor["models"]!.AsArray());
    }

    [Fact]
    public async Task A_successful_probe_runs_once_per_process()
    {
        var created = new List<CatalogVendor>();
        var cache = new CatalogCache((id, _) =>
        {
            var vendor = new CatalogVendor(id!, available: true, new VendorCatalog([], CatalogSource.Live));
            created.Add(vendor);
            return vendor;
        });

        cache.BeginProbing(_workspace);
        await cache.GetAsync("codex", _workspace, CancellationToken.None);
        await cache.GetAsync("codex", _workspace, CancellationToken.None);

        Assert.Equal(3, created.Count);
        Assert.All(created, vendor => Assert.Equal(1, vendor.Probes));
    }

    /// <summary>
    /// A failed probe is not cached: its cause — a missing binary, a sign-in — is fixable
    /// mid-session, and the next forge.models call should see the fix.
    /// </summary>
    [Fact]
    public async Task A_failed_probe_is_retried_on_the_next_request()
    {
        var vendor = new CatalogVendor("codex", available: false, new VendorCatalog([], CatalogSource.Live),
            detail: "codex is not signed in — run 'codex login'");
        var cache = new CatalogCache((_, _) => vendor);

        var first = await cache.GetAsync("codex", _workspace, CancellationToken.None);
        vendor.Available = true;
        var second = await cache.GetAsync("codex", _workspace, CancellationToken.None);

        Assert.False(first.Available);
        Assert.True(second.Available);
        Assert.Equal(2, vendor.Probes);
    }

    private RunDirectory NewRun(string runId)
    {
        var run = RunDirectory.Create(_workspace, runId);
        run.WriteState(new RunState(runId, _workspace, "Text", DateTimeOffset.Now, 0, 5));
        return run;
    }

    private sealed class CatalogVendor(string id, bool available, VendorCatalog catalog, string detail = "ready")
        : IVendor
    {
        public int Probes;
        public bool Available = available;

        public string Id => id;
        public VendorCatalog Catalog { get; private set; } = new([], catalog.Source);

        public Task<VendorReadiness> ProbeAsync(CancellationToken ct)
        {
            Probes++;
            if (Available) Catalog = catalog;
            return Task.FromResult(new VendorReadiness(Available, detail));
        }

        public Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken,
                                               CancellationToken ct) =>
            throw new NotSupportedException("catalog tests never start a session");
    }
}
