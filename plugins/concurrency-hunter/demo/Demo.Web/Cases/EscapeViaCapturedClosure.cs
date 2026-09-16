using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.EscapeViaCapturedClosure;

public sealed class Note
{
    public string? Text { get; set; }
}

public sealed class CallbackHolder
{
    public Action<string>? OnMessage { get; set; }
}

/// <summary>A local object escapes by being captured in a lambda stored in a singleton: an action invokes the
/// lambda, whose write carries the symbol of the worker method that declares it, while the worker writes the
/// object too; the action also reads the delegate slot the worker writes.</summary>
public sealed class NoteWorker : BackgroundService
{
    private readonly CallbackHolder _holder;

    public NoteWorker(CallbackHolder holder) => _holder = holder;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var note = new Note();
        _holder.OnMessage = text => note.Text = text;
        note.Text = "worker";
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/escape-via-captured-closure")]
public sealed class NoteController : ControllerBase
{
    private readonly CallbackHolder _holder;

    public NoteController(CallbackHolder holder) => _holder = holder;

    [HttpPost]
    public void Post(string text) => _holder.OnMessage!(text);
}

public static class EscapeViaCapturedClosureCase
{
    public static IServiceCollection AddEscapeViaCapturedClosure(this IServiceCollection services) =>
        services.AddSingleton<CallbackHolder>()
                .AddHostedService<NoteWorker>();
}
