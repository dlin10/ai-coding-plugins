using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.SingletonConfiguredInConstructor;

/// <summary>Hard negative: a settable property written only while the container constructs the singleton,
/// then only read by actions.</summary>
public sealed class SiteCatalog
{
    public SiteCatalog() => Title = "Demo";

    public string Title { get; set; }
}

[ApiController]
[Route("cases/singleton-configured-in-constructor")]
public sealed class CatalogController : ControllerBase
{
    private readonly SiteCatalog _catalog;

    public CatalogController(SiteCatalog catalog) => _catalog = catalog;

    [HttpGet]
    public string Get() => _catalog.Title;
}

public static class SingletonConfiguredInConstructorCase
{
    public static IServiceCollection AddSingletonConfiguredInConstructor(this IServiceCollection services) =>
        services.AddSingleton<SiteCatalog>();
}
