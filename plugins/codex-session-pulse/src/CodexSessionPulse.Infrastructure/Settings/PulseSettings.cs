namespace CodexSessionPulse.Infrastructure.Settings;

/// <summary>Where the overlay sits against the Codex window.</summary>
public enum OverlayAnchor
{
    /// <summary>Centred at the bottom of the Codex window, shifted by the offsets.</summary>
    BottomOfCodexWindow,
}

/// <summary>The overlay colour theme.</summary>
public enum OverlayTheme
{
    /// <summary>Follows the Windows app theme.</summary>
    System,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>
/// The P0 user settings (spec §8.1), stored as <c>settings.json</c>. There is no mode, pinned session or start-with-Windows
/// setting in P0, and the file never holds secrets or chat text.
/// </summary>
public sealed record PulseSettings
{
    /// <summary>The schema version this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The smallest accepted TTL estimate, in minutes.</summary>
    public const int MinTtlMinutes = 1;

    /// <summary>The largest accepted TTL estimate, in minutes.</summary>
    public const int MaxTtlMinutes = 720;

    /// <summary>The largest accepted offset magnitude, in DIP.</summary>
    public const int MaxOffset = 4000;

    /// <summary>Gets the schema version of the file.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Gets the rollouts root to use instead of the default <c>~\.codex\sessions</c>, or <see langword="null"/>.</summary>
    public string? SessionRootOverride { get; init; }

    /// <summary>Gets the process names of Codex Desktop, without <c>.exe</c>.</summary>
    public IReadOnlyList<string> CodexProcessNames { get; init; } = ["Codex", "ChatGPT"];

    /// <summary>Gets a substring the Codex window title must contain, or <see langword="null"/> for any title.</summary>
    public string? CodexWindowTitleFilter { get; init; }

    /// <summary>
    /// Gets the substrings of which the Codex executable path must contain one; empty accepts any path. The default
    /// excludes the ChatGPT consumer app, whose process is also <c>ChatGPT.exe</c> (docs/compatibility.md §6).
    /// </summary>
    public IReadOnlyList<string> CodexExecutablePathFilters { get; init; } = [@"\WindowsApps\OpenAI.Codex_"];

    /// <summary>Gets where the overlay sits.</summary>
    public OverlayAnchor Anchor { get; init; } = OverlayAnchor.BottomOfCodexWindow;

    /// <summary>Gets the horizontal offset in DIP; positive moves right.</summary>
    public int OffsetX { get; init; }

    /// <summary>Gets the offset of the overlay's bottom edge from the Codex window's bottom edge in DIP; negative moves up.</summary>
    public int OffsetY { get; init; } = -72;

    /// <summary>Gets a value indicating whether the overlay hides while Codex is not the foreground app.</summary>
    public bool HideWhenCodexInactive { get; init; } = true;

    /// <summary>Gets the colour theme.</summary>
    public OverlayTheme Theme { get; init; } = OverlayTheme.System;

    /// <summary>Gets the assumed cache TTL in minutes; it changes only the estimate, never observed usage.</summary>
    public int EstimatedCacheTtlMinutes { get; init; } = 30;

    /// <summary>Gets the cached-token count from which a read counts as a confirmed cache hit.</summary>
    public int MinCachedTokensForWarm { get; init; } = 1024;
}
