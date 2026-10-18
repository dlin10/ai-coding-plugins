using Microsoft.AspNetCore.Mvc;
using Polly;

namespace Demo.Web.Cases.PollyExecuteInvokesDelegate;

/// <summary>A singleton the delegate writes.</summary>
public sealed class State
{
    public int Hits;
}

/// <summary>Holds the pipeline the action executes through; registered as an instance, so it is built at startup, before any root.</summary>
public sealed class PipelineHolder
{
    public ResiliencePipeline Value { get; } = new ResiliencePipelineBuilder().Build();
}

/// <summary>Executes a delegate that counts on the singleton through a Polly pipeline: <c>Execute</c> runs the delegate before it returns,
/// in the action's own execution, so the action meets itself on <c>Hits</c>.</summary>
[ApiController]
[Route("cases/polly-execute-invokes-delegate")]
public sealed class PipelineController : ControllerBase
{
    private readonly PipelineHolder _pipeline;
    private readonly State _state;

    public PipelineController(PipelineHolder pipeline, State state)
    {
        _pipeline = pipeline;
        _state = state;
    }

    [HttpPost]
    public void Post() => _pipeline.Value.Execute(() => _state.Hits++);
}

public static class PollyExecuteInvokesDelegateCase
{
    public static IServiceCollection AddPollyExecuteInvokesDelegate(this IServiceCollection services) =>
        services.AddSingleton(new PipelineHolder())
                .AddSingleton<State>();
}
