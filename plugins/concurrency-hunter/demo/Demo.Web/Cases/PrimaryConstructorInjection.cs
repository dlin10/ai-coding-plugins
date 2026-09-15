using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.PrimaryConstructorInjection;

public sealed class QuotaState
{
    public string? Owner { get; set; }
}

/// <summary>A controller primary constructor can inject singleton state written by an overlapping action.</summary>
[ApiController]
[Route("cases/primary-constructor-injection")]
public sealed class QuotaController(QuotaState quota) : ControllerBase
{
    [HttpPost]
    public void Post(string owner) => quota.Owner = owner;
}

public static class PrimaryConstructorInjectionCase
{
    public static IServiceCollection AddPrimaryConstructorInjection(this IServiceCollection services) =>
        services.AddSingleton<QuotaState>();
}
