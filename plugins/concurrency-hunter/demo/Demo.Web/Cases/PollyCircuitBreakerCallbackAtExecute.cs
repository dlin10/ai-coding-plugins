using Microsoft.AspNetCore.Mvc;
using Polly;
using Polly.CircuitBreaker;

namespace Demo.Web.Cases.PollyCircuitBreakerCallbackAtExecute;

/// <summary>Builds a pipeline whose circuit breaker counts every time it opens. The singleton is registered as an instance, so the
/// constructor runs at startup, before any root; the callback runs inside <c>Execute</c> when the failures open the circuit.</summary>
public sealed class Breaker
{
    public int Opened;

    public Breaker()
    {
        Pipeline = new ResiliencePipelineBuilder()
                       .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                       {
                           FailureRatio = 0.5,
                           MinimumThroughput = 2,
                           OnOpened = _ =>
                           {
                               Opened++;
                               return default;
                           }
                       })
                       .Build();
    }

    public ResiliencePipeline Pipeline { get; }
}

[ApiController]
[Route("cases/polly-circuit-breaker-callback-at-execute")]
public sealed class BreakerController : ControllerBase
{
    private readonly Breaker _breaker;

    public BreakerController(Breaker breaker) => _breaker = breaker;

    /// <summary>Executes an operation that throws and catches what the pipeline rethrows.</summary>
    [HttpPost]
    public void Post()
    {
        try
        {
            _breaker.Pipeline.Execute(() => throw new InvalidOperationException("The dependency is down."));
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public static class PollyCircuitBreakerCallbackAtExecuteCase
{
    public static IServiceCollection AddPollyCircuitBreakerCallbackAtExecute(this IServiceCollection services) =>
        services.AddSingleton(new Breaker());
}
