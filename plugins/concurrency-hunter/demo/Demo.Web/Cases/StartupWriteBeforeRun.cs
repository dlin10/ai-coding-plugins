using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.StartupWriteBeforeRun;

public sealed class SiteBanner
{
    public string? Text { get; set; }
}

/// <summary>Hard negative: the only write runs from Program before the host starts, which is no execution root.</summary>
[ApiController]
[Route("cases/startup-write-before-run")]
public sealed class BannerController : ControllerBase
{
    private readonly SiteBanner _banner;

    public BannerController(SiteBanner banner) => _banner = banner;

    [HttpGet]
    public string? Get() => _banner.Text;
}

public static class StartupWriteBeforeRunCase
{
    public static IServiceCollection AddStartupWriteBeforeRun(this IServiceCollection services) =>
        services.AddSingleton<SiteBanner>();

    public static WebApplication ConfigureStartupWriteBeforeRun(this WebApplication app)
    {
        app.Services.GetRequiredService<SiteBanner>().Text = "Welcome";
        return app;
    }
}
