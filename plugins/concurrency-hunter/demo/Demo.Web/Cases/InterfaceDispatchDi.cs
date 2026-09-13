using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.InterfaceDispatchDi;

public interface IVisitSink
{
    void Record(string path);
}

/// <summary>The only registered implementation: the DI binding resolves the interface call to it.</summary>
public sealed class LastVisitSink : IVisitSink
{
    private string? _lastPath;

    public void Record(string path) => _lastPath = path;
}

[ApiController]
[Route("cases/interface-dispatch-di")]
public sealed class SinkController : ControllerBase
{
    private readonly IVisitSink _sink;

    public SinkController(IVisitSink sink) => _sink = sink;

    [HttpPost]
    public void Post(string path) => _sink.Record(path);
}

public static class InterfaceDispatchDiCase
{
    public static IServiceCollection AddInterfaceDispatchDi(this IServiceCollection services) =>
        services.AddSingleton<IVisitSink, LastVisitSink>();
}
