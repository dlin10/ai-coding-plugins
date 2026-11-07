using System.Globalization;
using System.Windows;
using CodexSessionPulse.Infrastructure.Settings;
using Microsoft.Win32;

namespace CodexSessionPulse.App.SettingsUi;

/// <summary>The Settings dialog (spec §8.1). An ordinary window that may take focus; the overlay stays non-activating.</summary>
internal partial class SettingsWindow : Window
{
    private readonly PulseSettings _current;
    private readonly Func<PulseSettings, IReadOnlyList<string>> _save;
    private readonly string _userProfile;

    /// <summary>Creates the dialog filled from the current settings.</summary>
    /// <param name="current">The settings in effect.</param>
    /// <param name="save">Validates and writes new settings, returning the problem codes.</param>
    /// <param name="userProfile">The user profile, against which the rollouts folder is checked.</param>
    public SettingsWindow(PulseSettings current, Func<PulseSettings, IReadOnlyList<string>> save, string userProfile)
    {
        InitializeComponent();
        _current = current;
        _save = save;
        _userProfile = userProfile;

        SessionRoot.Text = current.SessionRootOverride ?? string.Empty;
        ProcessNames.Text = string.Join(", ", current.CodexProcessNames);
        PathFilters.Text = string.Join("; ", current.CodexExecutablePathFilters);
        TitleFilter.Text = current.CodexWindowTitleFilter ?? string.Empty;
        OffsetX.Text = current.OffsetX.ToString(CultureInfo.InvariantCulture);
        OffsetY.Text = current.OffsetY.ToString(CultureInfo.InvariantCulture);
        Theme.ItemsSource = Enum.GetValues<OverlayTheme>();
        Theme.SelectedItem = current.Theme;
        TtlMinutes.Text = current.EstimatedCacheTtlMinutes.ToString(CultureInfo.InvariantCulture);
        MinCachedTokens.Text = current.MinCachedTokensForWarm.ToString(CultureInfo.InvariantCulture);
        KeepVisible.IsChecked = !current.HideWhenCodexInactive;
    }

    /// <summary>Gets the settings that were saved, or <see langword="null"/> when the dialog was cancelled.</summary>
    public PulseSettings? Saved { get; private set; }

    /// <summary>Asks for a rollouts folder and checks it, confirming one outside the user profile.</summary>
    /// <param name="owner">The owner window, or <see langword="null"/>.</param>
    /// <param name="userProfile">The user profile.</param>
    /// <param name="initial">The folder to start in, or <see langword="null"/>.</param>
    /// <returns>The accepted folder, or <see langword="null"/> when the user cancelled or the folder was refused.</returns>
    public static string? PickSessionRoot(Window? owner, string userProfile, string? initial)
    {
        OpenFolderDialog dialog = new() { Title = "Choose the Codex rollouts folder", InitialDirectory = initial ?? string.Empty };

        if (dialog.ShowDialog(owner) != true)
        {
            return null;
        }

        return ConfirmSessionRoot(owner, dialog.FolderName, userProfile) ? dialog.FolderName : null;
    }

    /// <summary>Explains a refused folder, or asks to confirm one outside the user profile.</summary>
    /// <param name="owner">The owner window, or <see langword="null"/>.</param>
    /// <param name="path">The folder.</param>
    /// <param name="userProfile">The user profile.</param>
    /// <returns><see langword="true"/> when the folder may be saved.</returns>
    private static bool ConfirmSessionRoot(Window? owner, string path, string userProfile)
    {
        SessionRootVerdict verdict = SessionRootCheck.Check(path, userProfile);
        string? refusal = verdict switch
        {
            SessionRootVerdict.NotAbsolute => "The rollouts folder must be an absolute path.",
            SessionRootVerdict.NotFound => "The rollouts folder does not exist.",
            SessionRootVerdict.ReparsePoint => "The rollouts folder or one of its parents is a symbolic link or junction, which is not allowed.",
            _ => null,
        };

        if (refusal is not null)
        {
            Show(owner, refusal, MessageBoxButton.OK, MessageBoxImage.Warning);

            return false;
        }

        return verdict == SessionRootVerdict.Ok
               || Show(owner, "The rollouts folder is outside your user profile. Use it anyway?", MessageBoxButton.YesNo, MessageBoxImage.Question)
                  == MessageBoxResult.Yes;
    }

    private static MessageBoxResult Show(Window? owner, string text, MessageBoxButton buttons, MessageBoxImage image)
    {
        return owner is null
            ? MessageBox.Show(text, "Codex Session Pulse", buttons, image)
            : MessageBox.Show(owner, text, "Codex Session Pulse", buttons, image);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        string? folder = PickSessionRoot(this, _userProfile, string.IsNullOrWhiteSpace(SessionRoot.Text) ? null : SessionRoot.Text);

        if (folder is not null)
        {
            SessionRoot.Text = folder;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryInt(OffsetX.Text, out int offsetX) || !TryInt(OffsetY.Text, out int offsetY)
            || !TryInt(TtlMinutes.Text, out int ttl) || !TryInt(MinCachedTokens.Text, out int minCached))
        {
            Problems.Text = "Offsets, TTL and the token threshold must be whole numbers.";

            return;
        }

        string? root = string.IsNullOrWhiteSpace(SessionRoot.Text) ? null : SessionRoot.Text.Trim();

        if (root is not null && root != _current.SessionRootOverride && !ConfirmSessionRoot(this, root, _userProfile))
        {
            return;
        }

        PulseSettings candidate = _current with
        {
            SessionRootOverride = root,
            CodexProcessNames = Split(ProcessNames.Text, ','),
            CodexExecutablePathFilters = Split(PathFilters.Text, ';'),
            CodexWindowTitleFilter = string.IsNullOrWhiteSpace(TitleFilter.Text) ? null : TitleFilter.Text.Trim(),
            OffsetX = offsetX,
            OffsetY = offsetY,
            Theme = Theme.SelectedItem is OverlayTheme theme ? theme : OverlayTheme.System,
            EstimatedCacheTtlMinutes = ttl,
            MinCachedTokensForWarm = minCached,
            HideWhenCodexInactive = KeepVisible.IsChecked != true,
        };

        IReadOnlyList<string> problems = _save(candidate);

        if (problems.Count > 0)
        {
            Problems.Text = "Not saved: " + string.Join(", ", problems);

            return;
        }

        Saved = candidate;
        DialogResult = true;
    }

    private static bool TryInt(string text, out int value) => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string[] Split(string text, char separator) =>
        text.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
