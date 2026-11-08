using System.Windows;
using System.Windows.Media;
using CodexSessionPulse.Infrastructure.Settings;
using Microsoft.Win32;

namespace CodexSessionPulse.App.Overlay;

/// <summary>The overlay's light and dark brushes, published as dynamic resources.</summary>
internal static class ThemePalette
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Decides whether the dark palette applies.</summary>
    /// <param name="theme">The theme setting.</param>
    /// <returns><see langword="true"/> for dark.</returns>
    public static bool IsDark(OverlayTheme theme) => theme switch
    {
        OverlayTheme.Dark => true,
        OverlayTheme.Light => false,
        _ => SystemUsesDarkApps(),
    };

    /// <summary>Writes the palette into a resource dictionary.</summary>
    /// <param name="resources">The window resources.</param>
    /// <param name="dark">Whether to use the dark palette.</param>
    public static void Apply(ResourceDictionary resources, bool dark)
    {
        Set(resources, "Pulse.Background", dark ? "#F0202124" : "#F2FBFBFB");
        Set(resources, "Pulse.Border", dark ? "#33FFFFFF" : "#1F000000");
        Set(resources, "Pulse.Foreground", dark ? "#FFE8EAED" : "#FF1F1F1F");
        Set(resources, "Pulse.Muted", dark ? "#FF9AA0A6" : "#FF6B6F76");
        Set(resources, "Pulse.Track", dark ? "#33FFFFFF" : "#1F000000");
        Set(resources, "Pulse.Neutral", dark ? "#FF9AA0A6" : "#FF7A7F87");
        Set(resources, "Pulse.Warm", dark ? "#FF34C3A0" : "#FF0E8A6F");
        Set(resources, "Pulse.Expiring", dark ? "#FFF5A623" : "#FFC26A00");
        Set(resources, "Pulse.Miss", dark ? "#FFF2726F" : "#FFC62828");
        Set(resources, "Pulse.Error", dark ? "#FFF2726F" : "#FFC62828");
        Set(resources, "Pulse.ButtonBackground", dark ? "#1FFFFFFF" : "#0F000000");
    }

    /// <summary>Reads the Windows "app mode" setting.</summary>
    /// <returns><see langword="true"/> when apps use the dark mode.</returns>
    private static bool SystemUsesDarkApps()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);

        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static void Set(ResourceDictionary resources, string key, string color)
    {
        SolidColorBrush brush = new((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        resources[key] = brush;
    }
}
