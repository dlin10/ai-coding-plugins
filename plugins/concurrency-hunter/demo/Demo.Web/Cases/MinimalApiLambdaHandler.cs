namespace Demo.Web.Cases.MinimalApiLambdaHandler;

public sealed class SignalState
{
    public string? Value { get; set; }
}

/// <summary>A minimal-API handler written as a lambda: its access carries the symbol of the method that maps
/// it, and the POST races with itself.</summary>
public static class MinimalApiLambdaHandlerCase
{
    public static IServiceCollection AddMinimalApiLambdaHandler(this IServiceCollection services) =>
        services.AddSingleton<SignalState>();

    public static WebApplication MapMinimalApiLambdaHandler(this WebApplication app)
    {
        app.MapPost("/cases/minimal-api-lambda-handler", (SignalState state, string value) => state.Value = value);
        return app;
    }
}
