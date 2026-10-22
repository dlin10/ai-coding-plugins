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
    /// The selection, confirmed against the vendor's catalogue when it asks for Fast. A catalogue
    /// that is not there to ask — a vendor whose probe failed — confirms nothing.
    /// </summary>
    /// <param name="catalogs">The probed catalogues the confirmation reads.</param>
    /// <param name="vendor">The vendor the selection is for.</param>
    /// <param name="selection">The model, effort and speed the tool call asked for.</param>
    /// <param name="workspaceRoot">The workspace a fresh probe runs in.</param>
    /// <param name="ct">Stops waiting for the probe.</param>
    internal static async Task<Selection> ConfirmAsync(CatalogCache catalogs,
                                                       IVendor vendor,
                                                       Selection selection,
                                                       string workspaceRoot,
                                                       CancellationToken ct)
    {
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
