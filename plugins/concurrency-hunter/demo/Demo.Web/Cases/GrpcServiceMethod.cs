using Grpc.Core;

namespace Demo.Web.Cases.GrpcServiceMethod;

public sealed class GreetingLog
{
    public string? LastGreeting { get; set; }
}

/// <summary>A gRPC service method is a root like an action: it writes a singleton and overlaps itself.</summary>
public sealed class GreeterService : Greeter.GreeterBase
{
    private readonly GreetingLog _log;

    public GreeterService(GreetingLog log) => _log = log;

    public override Task<GreetReply> Greet(GreetRequest request, ServerCallContext context)
    {
        _log.LastGreeting = "hello";
        return Task.FromResult(new GreetReply());
    }
}

public static class GrpcServiceMethodCase
{
    public static IServiceCollection AddGrpcServiceMethod(this IServiceCollection services)
    {
        services.AddGrpc();
        return services.AddSingleton<GreetingLog>();
    }

    public static WebApplication MapGrpcServiceMethod(this WebApplication app)
    {
        app.MapGrpcService<GreeterService>();
        return app;
    }
}
