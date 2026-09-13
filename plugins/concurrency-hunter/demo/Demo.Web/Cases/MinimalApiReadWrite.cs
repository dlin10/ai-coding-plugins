namespace Demo.Web.Cases.MinimalApiReadWrite;

public sealed class FeatureFlags
{
    public string? Mode { get; set; }
}

/// <summary>Minimal-API handlers as method groups, so each access has a readable symbol: the POST races
/// with itself and with the GET.</summary>
public static class FeatureFlagEndpoints
{
    public static void Set(FeatureFlags flags, string mode) => flags.Mode = mode;

    public static string? Get(FeatureFlags flags) => flags.Mode;
}

public static class MinimalApiReadWriteCase
{
    private const string ROUTE = "/cases/minimal-api-read-write";

    public static IServiceCollection AddMinimalApiReadWrite(this IServiceCollection services) =>
        services.AddSingleton<FeatureFlags>();

    public static WebApplication MapMinimalApiReadWrite(this WebApplication app)
    {
        app.MapPost(ROUTE, FeatureFlagEndpoints.Set);
        app.MapGet(ROUTE, FeatureFlagEndpoints.Get);
        return app;
    }
}
