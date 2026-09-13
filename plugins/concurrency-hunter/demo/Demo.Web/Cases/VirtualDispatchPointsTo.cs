using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.VirtualDispatchPointsTo;

public abstract class Handler
{
    public abstract void Handle(string input);
}

public sealed class LoudHandler : Handler
{
    private string? _last;

    public override void Handle(string input) => _last = input;
}

/// <summary>Hard negative inside a positive case: class hierarchy analysis reaches this override, but no
/// allocation of it exists, so points-to must drop it from the dispatch.</summary>
public sealed class QuietHandler : Handler
{
    internal static string? LastInput;

    public override void Handle(string input) => LastInput = input;
}

public sealed class HandlerHost
{
    private readonly Handler _handler;

    public HandlerHost() => _handler = new LoudHandler();

    public void Dispatch(string input) => _handler.Handle(input);
}

[ApiController]
[Route("cases/virtual-dispatch-points-to")]
public sealed class HandlerController : ControllerBase
{
    private readonly HandlerHost _host;

    public HandlerController(HandlerHost host) => _host = host;

    [HttpPost]
    public void Post(string input) => _host.Dispatch(input);
}

public static class VirtualDispatchPointsToCase
{
    public static IServiceCollection AddVirtualDispatchPointsTo(this IServiceCollection services) =>
        services.AddSingleton<HandlerHost>();
}
