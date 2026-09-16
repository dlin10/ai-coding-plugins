using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.EscapeViaOutParameter;

public sealed class Memo
{
    public string? Title { get; set; }
}

/// <summary>A singleton hands its inner object out through an out parameter: the action writes it, racing
/// with itself and with the worker that writes it through the field.</summary>
public sealed class Editor
{
    private readonly Memo _memo = new();

    public void Open(out Memo memo) => memo = _memo;

    public void Rename(string title) => _memo.Title = title;
}

public sealed class EditorWorker : BackgroundService
{
    private readonly Editor _editor;

    public EditorWorker(Editor editor) => _editor = editor;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _editor.Rename("worker");
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/escape-via-out-parameter")]
public sealed class EditorController : ControllerBase
{
    private readonly Editor _editor;

    public EditorController(Editor editor) => _editor = editor;

    [HttpPut]
    public void Put(string title)
    {
        _editor.Open(out var memo);
        memo.Title = title;
    }
}

public static class EscapeViaOutParameterCase
{
    public static IServiceCollection AddEscapeViaOutParameter(this IServiceCollection services) =>
        services.AddSingleton<Editor>()
                .AddHostedService<EditorWorker>();
}
