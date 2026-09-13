using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.AliasTwoFields;

public sealed class Box
{
    public string? Value { get; set; }
}

/// <summary>One object reachable through two fields: a write through one and a read through the other
/// touch the same resource.</summary>
public sealed class Holder
{
    private readonly Box _primary;
    private readonly Box _mirror;

    public Holder()
    {
        _primary = new Box();
        _mirror = _primary;
    }

    public void WritePrimary(string value) => _primary.Value = value;

    public string? ReadMirror() => _mirror.Value;
}

public sealed class PrimaryWriterWorker : BackgroundService
{
    private readonly Holder _holder;

    public PrimaryWriterWorker(Holder holder) => _holder = holder;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _holder.WritePrimary("worker");
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/alias-two-fields")]
public sealed class MirrorController : ControllerBase
{
    private readonly Holder _holder;

    public MirrorController(Holder holder) => _holder = holder;

    [HttpGet]
    public string? Get() => _holder.ReadMirror();
}

public static class AliasTwoFieldsCase
{
    public static IServiceCollection AddAliasTwoFields(this IServiceCollection services) =>
        services.AddSingleton<Holder>()
                .AddHostedService<PrimaryWriterWorker>();
}
