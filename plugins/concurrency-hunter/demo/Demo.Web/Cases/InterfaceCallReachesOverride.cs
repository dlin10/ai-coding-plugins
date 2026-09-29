using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.InterfaceCallReachesOverride;

public sealed class Tally
{
    public int Hits;
}

public sealed class Score
{
    public int Points;
}

public interface IStep
{
    void Run();
}

public class QuietStep : IStep
{
    public virtual void Run() { }
}

public sealed class CountingStep : QuietStep
{
    private readonly Tally _tally;

    public CountingStep(Tally tally) => _tally = tally;

    public override void Run() => _tally.Hits++;
}

public abstract class PlainStep : IStep
{
    public abstract void Run();
}

public sealed class ScoringStep : PlainStep
{
    private readonly Score _score;

    public ScoringStep(Score score) => _score = score;

    public override void Run() => _score.Points++;
}

[ApiController]
[Route("cases/interface-call-reaches-override")]
public sealed class StepController : ControllerBase
{
    private readonly Tally _tally;
    private readonly Score _score;

    public StepController(Tally tally, Score score)
    {
        _tally = tally;
        _score = score;
    }

    [HttpPost("virtual")]
    public void PostVirtual()
    {
        IStep step = new CountingStep(_tally);
        step.Run();
    }

    [HttpPost("abstract")]
    public void PostAbstract()
    {
        IStep step = new ScoringStep(_score);
        step.Run();
    }
}

public static class InterfaceCallReachesOverrideCase
{
    public static IServiceCollection AddInterfaceCallReachesOverride(this IServiceCollection services) =>
        services.AddSingleton<Tally>().AddSingleton<Score>();
}
