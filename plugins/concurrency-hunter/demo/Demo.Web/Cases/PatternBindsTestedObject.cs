using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.PatternBindsTestedObject;

public sealed class Slot
{
    public int Count;
}

public sealed class Board
{
    public Board() => Entry = new Slot();

    public object Entry { get; }
}

public sealed class Meter
{
    public int Level;

    public bool Above(Meter other) => other is { Level: > 0 };
}

public sealed class Panel
{
    public Panel()
    {
        Main = new Meter();
        Spare = new Meter();
    }

    public Meter Main { get; }
    public Meter Spare { get; }
}

[ApiController]
[Route("cases/pattern-binds-tested-object")]
public sealed class PatternController : ControllerBase
{
    private readonly Board _board;
    private readonly Panel _panel;

    public PatternController(Board board, Panel panel)
    {
        _board = board;
        _panel = panel;
    }

    [HttpPost]
    public void Post()
    {
        if (_board.Entry is Slot slot)
            slot.Count++;
    }

    [HttpGet]
    public bool Get() => _panel.Main.Above(_panel.Spare);
}

public sealed class PanelWorker : BackgroundService
{
    private readonly Panel _panel;

    public PanelWorker(Panel panel) => _panel = panel;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _panel.Main.Level = 1;
        _panel.Spare.Level = 1;
        return Task.CompletedTask;
    }
}

public static class PatternBindsTestedObjectCase
{
    public static IServiceCollection AddPatternBindsTestedObject(this IServiceCollection services) =>
        services.AddSingleton<Board>().AddSingleton<Panel>().AddHostedService<PanelWorker>();
}
