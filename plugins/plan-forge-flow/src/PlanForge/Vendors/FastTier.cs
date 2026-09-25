using PlanForge.Run;

namespace PlanForge.Vendors;

/// <summary>
/// The one check between a Fast request and a launch. Model and effort stay advisory — a wrong one
/// fails loudly — but a Fast request nothing confirmed fails quietly and costs money either way, so
/// it is refused here, before any Worker starts. See docs/adr/0023.
/// </summary>
internal static class FastTier
{
    /// <summary>
    /// The selection in the vendor's own spelling, confirmed against its catalogue when it asks for
    /// Fast. A catalogue that is not there to ask — a vendor whose probe failed — confirms nothing.
    /// </summary>
    internal static async Task<Selection> ConfirmAsync(CatalogCache catalogs,
                                                       IVendor vendor,
                                                       Selection requested,
                                                       string workspaceRoot,
                                                       CancellationToken ct)
    {
        var selection = vendor.Normalize(requested);
        if (!selection.Fast) return selection;

        var report = await catalogs.GetAsync(vendor.Id, workspaceRoot, ct).ConfigureAwait(false);
        var refusal = report.Available
            ? vendor.RefuseFast(selection, report.Catalog)
            : $"{vendor.Id} is unavailable: {report.Detail}";

        return refusal is null
            ? selection
            : throw new ArgumentRejectedException(
                $"fast is refused for {vendor.Id} model \"{selection.Model}\": {refusal}. Ask again without fast.");
    }
}
