using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Demo.Web.Cases.ProjectOpaqueSwitchesOffGenerated;

public sealed class Settings
{
    public int Level;
}

/// <summary>The singleton builds its <c>Settings</c> in the constructor, so every action reaches the same one.</summary>
public sealed class SettingsBox
{
    public Settings Settings { get; }

    public SettingsBox() => Settings = new Settings();
}

/// <summary>Hands the singleton's <c>Settings</c> to <c>Options.Create</c>, a member the committed generated models describe. The project's
/// <c>opaque</c> entry for the same member stands over that model: the call is opaque again, its unknown effect reaches the <c>Settings</c>,
/// and the read of <c>Level</c> in the other action meets it.</summary>
[ApiController]
[Route("cases/project-opaque-switches-off-generated")]
public sealed class SettingsController : ControllerBase
{
    private readonly SettingsBox _box;

    public SettingsController(SettingsBox box) => _box = box;

    [HttpPost]
    public void Post() => Options.Create(_box.Settings);

    [HttpGet]
    public int Get() => _box.Settings.Level;
}

public static class ProjectOpaqueSwitchesOffGeneratedCase
{
    public static IServiceCollection AddProjectOpaqueSwitchesOffGenerated(this IServiceCollection services) =>
        services.AddSingleton<SettingsBox>();
}
