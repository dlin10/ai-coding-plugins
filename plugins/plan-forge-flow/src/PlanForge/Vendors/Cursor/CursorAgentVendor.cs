using PlanForge.Infrastructure;

namespace PlanForge.Vendors.Cursor;

/// <summary>
/// The one vendor with no native schema support: structure is achieved by putting the schema in the
/// prompt and validating on our side, with one retry. This is the price of making structured output
/// a hard interface requirement, and the template for every future vendor that lacks it.
/// </summary>
internal sealed class CursorAgentVendor : IVendor
{
    private const string Command = "cursor-agent";

    private readonly string? _workingDirectory;

    public CursorAgentVendor(string? workingDirectory = null)
    {
        _workingDirectory = workingDirectory;
        Catalog = new VendorCatalog([]);
    }

    public string Id => "cursor";

    /// <summary>Filled by <see cref="ProbeAsync"/> — Cursor publishes a live model list.</summary>
    public VendorCatalog Catalog { get; private set; }

    internal static string Executable =>
        ExecutableResolver.Resolve(Command, FallbackDirectories())
            ?? throw new VendorException($"{Command} was not found on PATH or in its usual install directory");

    public async Task<VendorReadiness> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var spec = new ProcessSpec(Executable, ["--list-models"], _workingDirectory, string.Empty);
            var lines = await StreamingProcess.CollectAsync(spec, TimeSpan.FromMinutes(1), ct);

            Catalog = new VendorCatalog(ParseModels(lines));
            return new VendorReadiness(true, $"{Catalog.Models.Count} models");
        }
        catch (Exception error) when (error is VendorException or OperationCanceledException)
        {
            return new VendorReadiness(false, error.Message);
        }
    }

    public Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken, CancellationToken ct) =>
        Task.FromResult<IVendorSession>(new CursorAgentSession(role, selection, _workingDirectory, resumeToken));

    // "gpt-5.3-codex-high - Codex 5.3 High" — effort is baked into the id here rather than being a
    // separate flag, which is why joining model and effort is the vendor's job.
    internal static List<VendorModel> ParseModels(IEnumerable<string> lines)
    {
        var models = new List<VendorModel>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf(" - ", StringComparison.Ordinal);
            if (separator <= 0) continue;

            var id = trimmed[..separator].Trim();
            if (id.Length == 0 || id.Contains(' ', StringComparison.Ordinal)) continue;

            models.Add(new VendorModel(id, []));
        }

        return models;
    }

    private static string[] FallbackDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return localAppData.Length == 0 ? [] : [Path.Combine(localAppData, "cursor-agent")];
    }
}
