using System.Text.Json;

namespace CodexSessionPulse.Infrastructure.Settings;

/// <summary>The outcome of reading the settings file.</summary>
/// <param name="Settings">The settings to use; invalid fields are replaced by their defaults.</param>
/// <param name="Problems">Machine-readable codes of what was wrong, for diagnostics; never a value from the file.</param>
public sealed record SettingsLoadResult(PulseSettings Settings, IReadOnlyList<string> Problems);

/// <summary>Reads and atomically writes <c>settings.json</c> (spec §8.1).</summary>
/// <param name="path">The settings file.</param>
/// <param name="userProfile">The user profile, against which the rollouts root override is checked.</param>
public sealed class SettingsStore(string path, string userProfile)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new LenientEnumConverter<OverlayAnchor>(), new LenientEnumConverter<OverlayTheme>() },
    };

    /// <summary>Gets the settings file.</summary>
    public string Path => path;

    /// <summary>Reads the settings. A missing file gives the defaults; it is not created.</summary>
    /// <returns>The settings and the problems found.</returns>
    public SettingsLoadResult Load()
    {
        if (!File.Exists(path))
        {
            return new SettingsLoadResult(new PulseSettings(), []);
        }

        PulseSettings? read;

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            read = JsonSerializer.Deserialize<PulseSettings>(stream, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(new PulseSettings(), [exception is JsonException ? "settings-invalid-json" : "settings-unreadable"]);
        }

        if (read is null)
        {
            return new SettingsLoadResult(new PulseSettings(), ["settings-invalid-json"]);
        }

        if (read.SchemaVersion > PulseSettings.CurrentSchemaVersion)
        {
            return new SettingsLoadResult(new PulseSettings(), ["settings-schema-newer"]);
        }

        List<string> problems = [];
        PulseSettings settings = Sanitize(MigrateV1(read, problems), problems);

        if (settings.SessionRootOverride is { } root
            && SessionRootCheck.Check(root, userProfile) is not (SessionRootVerdict.Ok or SessionRootVerdict.OutsideProfile))
        {
            // An override outside the profile was confirmed when it was saved; one that is missing or a reparse point is dropped.
            problems.Add("session-root-rejected");
            settings = settings with { SessionRootOverride = null };
        }

        return new SettingsLoadResult(settings with { SchemaVersion = PulseSettings.CurrentSchemaVersion }, problems);
    }

    /// <summary>
    /// Validates and writes the settings atomically: a temporary file next to the target, flushed to disk, then moved over it.
    /// The caller confirms an override outside the profile before saving.
    /// </summary>
    /// <param name="settings">The settings to write.</param>
    /// <returns>The validation problems; the file is written only when there are none.</returns>
    public IReadOnlyList<string> Save(PulseSettings settings)
    {
        List<string> problems = [];
        Sanitize(settings, problems);

        if (settings.SessionRootOverride is { } root
            && SessionRootCheck.Check(root, userProfile) is not (SessionRootVerdict.Ok or SessionRootVerdict.OutsideProfile))
        {
            problems.Add("session-root-rejected");
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        string directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temp = path + ".tmp";

        using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, settings with { SchemaVersion = PulseSettings.CurrentSchemaVersion }, JsonOptions);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);

        return [];
    }

    /// <summary>
    /// Moves a version 1 file to the current placement. Version 1 knew only <see cref="OverlayAnchor.BottomOfCodexWindow"/>,
    /// with the vertical offset measured from the window's bottom edge (default -170); its offsets made up for a rule that
    /// did not know the input card, so they are dropped, and the anchor takes the current default.
    /// </summary>
    /// <param name="settings">The settings as read.</param>
    /// <param name="problems">Receives <c>settings-v1-offsets-dropped</c> when offsets other than version 1's defaults are dropped.</param>
    /// <returns>The settings with the current placement fields.</returns>
    private static PulseSettings MigrateV1(PulseSettings settings, List<string> problems)
    {
        if (settings.SchemaVersion >= 2)
        {
            return settings;
        }

        if (settings.OffsetX != 0 || settings.OffsetY != -170)
        {
            problems.Add("settings-v1-offsets-dropped");
        }

        PulseSettings defaults = new();

        return settings with { Anchor = defaults.Anchor, OffsetX = defaults.OffsetX, OffsetY = defaults.OffsetY };
    }

    /// <summary>Replaces each invalid field by its default and records why.</summary>
    /// <param name="settings">The settings as read.</param>
    /// <param name="problems">Receives a code per invalid field.</param>
    /// <returns>The settings with valid fields only.</returns>
    private static PulseSettings Sanitize(PulseSettings settings, List<string> problems)
    {
        PulseSettings defaults = new();
        PulseSettings result = settings;

        if (settings.CodexProcessNames is null || settings.CodexProcessNames.Count == 0
            || settings.CodexProcessNames.Any(name => string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\\', '/']) >= 0))
        {
            problems.Add("process-names-invalid");
            result = result with { CodexProcessNames = defaults.CodexProcessNames };
        }

        if (settings.CodexExecutablePathFilters is null || settings.CodexExecutablePathFilters.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add("path-filters-invalid");
            result = result with { CodexExecutablePathFilters = defaults.CodexExecutablePathFilters };
        }

        if (!Enum.IsDefined(settings.Anchor))
        {
            problems.Add("anchor-invalid");
            result = result with { Anchor = defaults.Anchor };
        }

        if (!Enum.IsDefined(settings.Theme))
        {
            problems.Add("theme-invalid");
            result = result with { Theme = defaults.Theme };
        }

        if (Math.Abs(settings.OffsetX) > PulseSettings.MaxOffset || Math.Abs(settings.OffsetY) > PulseSettings.MaxOffset)
        {
            problems.Add("offset-out-of-range");
            result = result with { OffsetX = defaults.OffsetX, OffsetY = defaults.OffsetY };
        }

        if (settings.EstimatedCacheTtlMinutes is < PulseSettings.MinTtlMinutes or > PulseSettings.MaxTtlMinutes)
        {
            problems.Add("ttl-out-of-range");
            result = result with { EstimatedCacheTtlMinutes = defaults.EstimatedCacheTtlMinutes };
        }

        if (settings.MinCachedTokensForWarm < 0)
        {
            problems.Add("min-cached-tokens-invalid");
            result = result with { MinCachedTokensForWarm = defaults.MinCachedTokensForWarm };
        }

        return result;
    }
}
