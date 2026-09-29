using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LateReceiverBaseCall;

public sealed class Tally
{
    public int Hits;
}

public abstract class Runner
{
    public void Run(Action work) => Impl(work);

    protected abstract void Impl(Action work);
}

public sealed class DirectRunner : Runner
{
    protected override void Impl(Action work) => work();
}

public static class Runners
{
    public static Runner Create() => new DirectRunner();
}

[ApiController]
[Route("cases/late-receiver-base-call")]
public sealed class RunnerController : ControllerBase
{
    private readonly Tally _tally;

    public RunnerController(Tally tally) => _tally = tally;

    [HttpPost]
    public void Post() => Runners.Create().Run(() => _tally.Hits++);
}

public static class LateReceiverBaseCallCase
{
    public static IServiceCollection AddLateReceiverBaseCall(this IServiceCollection services) =>
        services.AddSingleton<Tally>();
}
