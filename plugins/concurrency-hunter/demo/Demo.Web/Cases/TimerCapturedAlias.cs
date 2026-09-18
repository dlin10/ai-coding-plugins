using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.TimerCapturedAlias;

public sealed class Banner
{
    public string? Text { get; set; }
}

/// <summary>A periodic <see cref="Timer"/> callback lambda captures the singleton's banner through a local and
/// writes it; an action reads the banner through the singleton.</summary>
public sealed class BannerRotator
{
    private readonly Timer _timer;

    public BannerRotator()
    {
        var banner = Banner;
        _timer = new Timer(_ => banner.Text = "rotated", null, 0, 1000);
    }

    public Banner Banner { get; } = new();
}

[ApiController]
[Route("cases/timer-captured-alias")]
public sealed class BannerController : ControllerBase
{
    private readonly BannerRotator _rotator;

    public BannerController(BannerRotator rotator) => _rotator = rotator;

    [HttpGet]
    public string? Get() => _rotator.Banner.Text;
}

public static class TimerCapturedAliasCase
{
    public static IServiceCollection AddTimerCapturedAlias(this IServiceCollection services) =>
        services.AddSingleton<BannerRotator>();
}
