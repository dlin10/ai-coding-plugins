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
        Catalog = new VendorCatalog([], Live: true);
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

            Catalog = new VendorCatalog(ParseModels(lines), Live: true);
            return new VendorReadiness(true, $"{Catalog.Models.Count} model families");
        }
        catch (Exception error) when (error is VendorException or OperationCanceledException)
        {
            return new VendorReadiness(false, error.Message);
        }
    }

    public Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken, CancellationToken ct) =>
        Task.FromResult<IVendorSession>(new CursorAgentSession(role, selection, _workingDirectory, resumeToken));

    // "gpt-5.3-codex-high - Codex 5.3 High" — effort is baked into the id here rather than being a
    // separate flag, which is why joining model and effort is the vendor's job. The raw list names
    // every effort and speed variant on its own line (~200 of them); the interview wants families,
    // so the ids are collapsed: strip "-fast", then a known effort suffix, and group what is left.
    // Every effort a family advertises is a variant the list actually contained, which is what
    // keeps the suffix join in CursorAgentSession honest — it can only rebuild observed ids. The
    // CLI's bracket-override syntax ("model[effort=high]") is not an alternative: measured on
    // 2026-08-19, it rejects even its own documented example with "Cannot use this model".
    internal static List<VendorModel> ParseModels(IEnumerable<string> lines)
    {
        var families = new List<Family>();
        var byBase = new Dictionary<string, Family>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf(" - ", StringComparison.Ordinal);
            if (separator <= 0) continue;

            var id = trimmed[..separator].Trim();
            if (id.Length == 0 || id.Contains(' ', StringComparison.Ordinal)) continue;

            var (baseId, effort) = SplitEffort(id);
            if (!byBase.TryGetValue(baseId, out var family))
            {
                family = new Family(baseId);
                byBase.Add(baseId, family);
                families.Add(family);
            }

            family.Efforts.Add(effort);
            if (effort == DefaultVariant) family.DisplayName = trimmed[(separator + 3)..].Trim();
        }

        // Newest family first; the vendor's own order is not recency. Ids without a version keep
        // the vendor's order at the tail, and ties inside a version keep it too (the sort is stable).
        return
        [
            .. families.Where(family => family.Version.Length > 0)
                       .OrderByDescending(family => family.Version, VersionOrder.Instance)
                       .Concat(families.Where(family => family.Version.Length == 0))
                       .Select(family => family.ToModel())
        ];
    }

    private const string DefaultVariant = "default";

    private static readonly string[] _effortLevels = ["low", "medium", "high", "xhigh", "max", "ultra"];

    /// <summary>
    /// "gpt-5.3-codex-xhigh-fast" → base "gpt-5.3-codex", effort "xhigh-fast". A bare id is the
    /// family's "default" variant, picked by leaving the effort unset.
    /// </summary>
    private static (string BaseId, string Effort) SplitEffort(string id)
    {
        var fast = id.EndsWith("-fast", StringComparison.Ordinal);
        var trimmed = fast ? id[..^"-fast".Length] : id;

        foreach (var level in _effortLevels)
        {
            if (!trimmed.EndsWith($"-{level}", StringComparison.Ordinal)) continue;
            return (trimmed[..^(level.Length + 1)], fast ? $"{level}-fast" : level);
        }

        return (trimmed, fast ? "fast" : DefaultVariant);
    }

    /// <summary>
    /// The version is the first run of numeric tokens, each contributing its dot-separated
    /// segments: "claude-opus-4-8" is the two-segment 4.8 written with dashes, not the integer 48,
    /// and "gpt-5.3-codex" carries 5.3 inside one token. Ids without one ("auto") return empty.
    /// </summary>
    internal static int[] VersionSegments(string id)
    {
        var segments = new List<int>();
        foreach (var token in id.Split('-'))
        {
            var parts = token.Split('.');
            if (parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit)))
                segments.AddRange(parts.Select(int.Parse));
            else if (segments.Count > 0) break;
        }

        return [.. segments];
    }

    private sealed class Family(string id)
    {
        public string Id { get; } = id;
        public int[] Version { get; } = VersionSegments(id);
        public List<string> Efforts { get; } = [];
        public string? DisplayName { get; set; }

        public VendorModel ToModel() =>
            new(Id, Efforts, DisplayName,
                DefaultEffort: Efforts.Contains(DefaultVariant) ? DefaultVariant : null,
                IsDefault: DisplayName?.Contains("default", StringComparison.OrdinalIgnoreCase) is true);
    }

    private sealed class VersionOrder : IComparer<int[]>
    {
        public static readonly VersionOrder Instance = new();

        public int Compare(int[]? left, int[]? right)
        {
            for (var index = 0; index < Math.Max(left!.Length, right!.Length); index++)
            {
                var difference = Segment(left, index).CompareTo(Segment(right, index));
                if (difference != 0) return difference;
            }

            return 0;
        }

        private static int Segment(int[] version, int index) => index < version.Length ? version[index] : 0;
    }

    private static string[] FallbackDirectories()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return localAppData.Length == 0 ? [] : [Path.Combine(localAppData, "cursor-agent")];
    }
}
