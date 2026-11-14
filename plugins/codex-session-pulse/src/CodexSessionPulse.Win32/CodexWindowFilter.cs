namespace CodexSessionPulse.Win32;

/// <summary>
/// The configurable rule that decides which top-level windows are Codex Desktop windows (spec §3.3).
/// </summary>
/// <param name="ProcessNames">Process names without <c>.exe</c>, matched case-insensitively; a window of any other process never matches.</param>
/// <param name="TitleFilter">A case-insensitive substring the window title must contain, or <see langword="null"/> or empty for any title.</param>
/// <param name="ExecutablePathFilters">
/// Case-insensitive substrings of which the executable path must contain at least one; an empty list accepts any path.
/// The default tells Codex Desktop (an MSIX package) from the ChatGPT consumer app, whose process has the same name.
/// </param>
public sealed record WindowDetectorOptions(IReadOnlyList<string> ProcessNames,
                                           string? TitleFilter,
                                           IReadOnlyList<string> ExecutablePathFilters)
{
    /// <summary>The process names measured in docs/compatibility.md §6: Codex Desktop runs as <c>ChatGPT.exe</c>.</summary>
    public static readonly IReadOnlyList<string> DefaultProcessNames = ["Codex", "ChatGPT"];

    /// <summary>The package folder of Codex Desktop under <c>Program Files\WindowsApps</c>.</summary>
    public static readonly IReadOnlyList<string> DefaultExecutablePathFilters = [@"\WindowsApps\OpenAI.Codex_"];

    /// <summary>Gets the defaults from the measurements.</summary>
    public static WindowDetectorOptions Default { get; } = new(DefaultProcessNames, null, DefaultExecutablePathFilters);

    /// <summary>Describes the rule for diagnostics; it holds no user data.</summary>
    /// <returns>A one-line description of the rule.</returns>
    public string Describe()
    {
        string title = string.IsNullOrEmpty(TitleFilter) ? "any" : $"contains \"{TitleFilter}\"";
        string paths = ExecutablePathFilters.Count == 0 ? "any" : string.Join(" | ", ExecutablePathFilters);

        return $"processes [{string.Join(", ", ProcessNames)}], title {title}, path {paths}, visible unowned top-level windows";
    }
}

/// <summary>What the enumerator learned about one top-level window.</summary>
/// <param name="Hwnd">The window handle.</param>
/// <param name="ProcessId">The id of the owning process.</param>
/// <param name="ExecutablePath">The full executable path of the owning process, or <see langword="null"/> when it could not be read.</param>
/// <param name="Title">The window title.</param>
/// <param name="IsVisible">Whether the window has <c>WS_VISIBLE</c>.</param>
/// <param name="IsCloaked">Whether DWM cloaks the window, as on another virtual desktop.</param>
/// <param name="HasOwner">Whether the window has an owner window.</param>
public readonly record struct WindowCandidate(nint Hwnd,
                                              int ProcessId,
                                              string? ExecutablePath,
                                              string Title,
                                              bool IsVisible,
                                              bool IsCloaked,
                                              bool HasOwner);

/// <summary>Applies <see cref="WindowDetectorOptions"/> to window candidates. Pure, so the rule is testable without windows.</summary>
public static class CodexWindowFilter
{
    /// <summary>Decides whether a window is a Codex Desktop window.</summary>
    /// <param name="candidate">The window.</param>
    /// <param name="options">The rule.</param>
    /// <returns><see langword="true"/> for a visible, uncloaked, unowned window of a matching process, path and title.</returns>
    public static bool IsCodexWindow(WindowCandidate candidate, WindowDetectorOptions options)
    {
        if (!candidate.IsVisible || candidate.IsCloaked || candidate.HasOwner || candidate.ExecutablePath is null)
        {
            return false;
        }

        string processName = Path.GetFileNameWithoutExtension(candidate.ExecutablePath);

        if (!options.ProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (options.ExecutablePathFilters.Count > 0
            && !options.ExecutablePathFilters.Any(filter => candidate.ExecutablePath.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return string.IsNullOrEmpty(options.TitleFilter)
               || candidate.Title.Contains(options.TitleFilter, StringComparison.OrdinalIgnoreCase);
    }
}
