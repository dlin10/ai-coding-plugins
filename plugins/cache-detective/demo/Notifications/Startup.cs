using Microsoft.Extensions.DependencyInjection;

namespace Notifications;

public static class Startup
{
    public static void Configure(IServiceCollection services) => services.AddHttpClient<ICatalogClient, CatalogClient>();
}
