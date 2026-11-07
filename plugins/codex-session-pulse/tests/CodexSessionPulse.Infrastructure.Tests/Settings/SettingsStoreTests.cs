using System.Diagnostics;
using System.Text.Json;
using CodexSessionPulse.Infrastructure.Settings;
using Xunit;

namespace CodexSessionPulse.Infrastructure.Tests.Settings;

/// <summary><c>settings.json</c>: defaults, atomic writes, validation and the rollouts root check (spec §8.1).</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csp-settings-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the temporary profile folder.</summary>
    public SettingsStoreTests() => Directory.CreateDirectory(_root);

    private string SettingsPath => Path.Combine(_root, ".codex-session-pulse", "settings.json");

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A missing file gives the defaults and is not created.</summary>
    [Fact]
    public void Load_WithoutFile_ReturnsDefaultsWithoutCreatingIt()
    {
        SettingsLoadResult result = Store().Load();

        Assert.Empty(result.Problems);
        Assert.Equal(30, result.Settings.EstimatedCacheTtlMinutes);
        Assert.Equal(-170, result.Settings.OffsetY);
        Assert.True(result.Settings.HideWhenCodexInactive);
        Assert.Equal(["Codex", "ChatGPT"], result.Settings.CodexProcessNames);
        Assert.False(File.Exists(SettingsPath));
    }

    /// <summary>Saved settings read back equal, as camelCase JSON with string enums and no temporary file left.</summary>
    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        PulseSettings settings = new()
        {
            SessionRootOverride = _root,
            CodexProcessNames = ["ChatGPT"],
            CodexWindowTitleFilter = "Chat",
            CodexExecutablePathFilters = [],
            OffsetX = 12,
            OffsetY = -40,
            HideWhenCodexInactive = false,
            Theme = OverlayTheme.Dark,
            EstimatedCacheTtlMinutes = 60,
        };

        Assert.Empty(Store().Save(settings));
        SettingsLoadResult loaded = Store().Load();
        string json = File.ReadAllText(SettingsPath);

        Assert.Empty(loaded.Problems);
        Assert.Equal(settings.SessionRootOverride, loaded.Settings.SessionRootOverride);
        Assert.Equal(settings.CodexProcessNames, loaded.Settings.CodexProcessNames);
        Assert.Equal(settings.CodexWindowTitleFilter, loaded.Settings.CodexWindowTitleFilter);
        Assert.Empty(loaded.Settings.CodexExecutablePathFilters);
        Assert.Equal((12, -40, false, OverlayTheme.Dark, 60),
                     (loaded.Settings.OffsetX, loaded.Settings.OffsetY, loaded.Settings.HideWhenCodexInactive, loaded.Settings.Theme, loaded.Settings.EstimatedCacheTtlMinutes));
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"theme\": \"Dark\"", json, StringComparison.Ordinal);
        Assert.Contains("\"anchor\": \"BottomOfCodexWindow\"", json, StringComparison.Ordinal);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    /// <summary>A TTL outside 1–720 minutes is refused on save and replaced by the default on load.</summary>
    /// <param name="minutes">The out-of-range TTL.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(721)]
    public void Ttl_OutsideRange_IsRejected(int minutes)
    {
        Assert.Equal(["ttl-out-of-range"], Store().Save(new PulseSettings { EstimatedCacheTtlMinutes = minutes }));
        Assert.False(File.Exists(SettingsPath));

        WriteRaw($$"""{ "schemaVersion": 1, "estimatedCacheTtlMinutes": {{minutes}}, "offsetY": -10 }""");
        SettingsLoadResult loaded = Store().Load();

        Assert.Equal(["ttl-out-of-range"], loaded.Problems);
        Assert.Equal(30, loaded.Settings.EstimatedCacheTtlMinutes);
        Assert.Equal(-10, loaded.Settings.OffsetY);
    }

    /// <summary>The §8.1 example loads; P0-irrelevant fields such as <c>autoWarmEnabled</c> are ignored.</summary>
    [Fact]
    public void Load_SpecExample_Succeeds()
    {
        WriteRaw("""
            {
              "schemaVersion": 1,
              "sessionRootOverride": null,
              "codexProcessNames": ["Codex", "ChatGPT"],
              "codexWindowTitleFilter": null,
              "anchor": "BottomOfCodexWindow",
              "offsetX": 0,
              "offsetY": -170,
              "hideWhenCodexInactive": true,
              "theme": "System",
              "estimatedCacheTtlMinutes": 30,
              "minCachedTokensForWarm": 1024,
              "autoWarmEnabled": false
            }
            """);

        SettingsLoadResult loaded = Store().Load();

        Assert.Empty(loaded.Problems);
        Assert.Equal(OverlayTheme.System, loaded.Settings.Theme);
        Assert.Equal([@"\WindowsApps\OpenAI.Codex_"], loaded.Settings.CodexExecutablePathFilters);
    }

    /// <summary>Broken JSON, an unknown enum value and a newer schema fall back to the defaults with a code.</summary>
    /// <param name="json">The file content.</param>
    /// <param name="problem">The expected problem code.</param>
    [Theory]
    [InlineData("{ not json", "settings-invalid-json")]
    [InlineData("""{ "theme": "Purple" }""", "theme-invalid")]
    [InlineData("""{ "anchor": 0 }""", "anchor-invalid")]
    [InlineData("""{ "schemaVersion": 2, "offsetY": 5 }""", "settings-schema-newer")]
    [InlineData("""{ "codexProcessNames": [] }""", "process-names-invalid")]
    public void Load_InvalidFile_FallsBackToDefaults(string json, string problem)
    {
        WriteRaw(json);

        SettingsLoadResult loaded = Store().Load();

        Assert.Equal([problem], loaded.Problems);
        Assert.Equal(-170, loaded.Settings.OffsetY);
        Assert.Equal(["Codex", "ChatGPT"], loaded.Settings.CodexProcessNames);
    }

    /// <summary>A bad enum value resets only its own field; the other fields of the file are kept.</summary>
    [Fact]
    public void Load_BadEnum_KeepsTheOtherFields()
    {
        WriteRaw("""{ "theme": "Darker", "offsetY": -10, "codexProcessNames": ["ChatGPT"] }""");

        SettingsLoadResult loaded = Store().Load();

        Assert.Equal(["theme-invalid"], loaded.Problems);
        Assert.Equal(OverlayTheme.System, loaded.Settings.Theme);
        Assert.Equal(-10, loaded.Settings.OffsetY);
        Assert.Equal(["ChatGPT"], loaded.Settings.CodexProcessNames);
    }

    /// <summary>An override that does not exist is dropped on load and refused on save.</summary>
    [Fact]
    public void SessionRoot_Missing_IsRejected()
    {
        string missing = Path.Combine(_root, "nope");

        Assert.Equal(["session-root-rejected"], Store().Save(new PulseSettings { SessionRootOverride = missing }));

        WriteRaw(JsonSerializer.Serialize(new { sessionRootOverride = missing }));
        SettingsLoadResult loaded = Store().Load();

        Assert.Equal(["session-root-rejected"], loaded.Problems);
        Assert.Null(loaded.Settings.SessionRootOverride);
    }

    /// <summary>The rollouts root check: inside the profile, relative, missing, outside the profile, and behind a junction.</summary>
    [Fact]
    public void SessionRootCheck_ClassifiesPaths()
    {
        string sessions = Directory.CreateDirectory(Path.Combine(_root, "sessions")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        string profile = Path.Combine(_root, "sessions", "..");

        Assert.Equal(SessionRootVerdict.Ok, SessionRootCheck.Check(sessions + Path.DirectorySeparatorChar, profile));
        Assert.Equal(SessionRootVerdict.NotAbsolute, SessionRootCheck.Check(@"sessions", profile));
        Assert.Equal(SessionRootVerdict.NotFound, SessionRootCheck.Check(Path.Combine(_root, "missing"), profile));
        Assert.Equal(SessionRootVerdict.OutsideProfile, SessionRootCheck.Check(outside, sessions));

        string junction = Path.Combine(_root, "link");
        using Process mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{sessions}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        Directory.CreateDirectory(Path.Combine(sessions, "2026"));

        try
        {
            Assert.Equal(SessionRootVerdict.ReparsePoint, SessionRootCheck.Check(junction, profile));
            Assert.Equal(SessionRootVerdict.ReparsePoint, SessionRootCheck.Check(Path.Combine(junction, "2026"), profile));
        }
        finally
        {
            // Removes the junction itself; a recursive delete of the parent is refused while it exists.
            Directory.Delete(junction);
        }
    }

    /// <summary>The root is the profile folder unless the development variable moves it.</summary>
    [Fact]
    public void PulsePaths_DefaultsToTheProfileRoot()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        PulsePaths defaults = PulsePaths.FromEnvironment(_ => null);
        PulsePaths moved = PulsePaths.FromEnvironment(name => name == PulsePaths.HomeVariable ? _root : null);

        Assert.Equal(Path.Combine(profile, ".codex-session-pulse", "settings.json"), defaults.SettingsFile);
        Assert.Equal(Path.Combine(profile, ".codex-session-pulse", "logs"), defaults.LogsDirectory);
        Assert.Equal(Path.Combine(_root, "settings.json"), moved.SettingsFile);
        Assert.Equal(defaults, PulsePaths.FromEnvironment(name => name == PulsePaths.HomeVariable ? @"relative\home" : null));
    }

    private SettingsStore Store() => new(SettingsPath, _root);

    private void WriteRaw(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, json);
    }
}
