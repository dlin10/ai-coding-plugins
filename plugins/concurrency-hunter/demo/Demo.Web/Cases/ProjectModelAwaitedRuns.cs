using Polly;
using Polly.Retry;

namespace Demo.Web.Cases.ProjectModelAwaitedRuns;

public sealed class PipelineHolder
{
    public ResiliencePipeline Pipeline { get; } = new ResiliencePipelineBuilder()
                                                    .AddRetry(new RetryStrategyOptions())
                                                    .Build();
}

public sealed class RunsState
{
    public int Done;
}

public sealed class RunsWorker : BackgroundService
{
    private readonly PipelineHolder _holder;
    private readonly RunsState _state;

    public RunsWorker(PipelineHolder holder, RunsState state)
    {
        _holder = holder;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _holder.Pipeline.ExecuteAsync(async ct =>
        {
            await Task.Yield();
            _state.Done = 1;
        }, stoppingToken);
        _state.Done = 2;
    }
}

public static class ProjectModelAwaitedRunsCase
{
    public static IServiceCollection AddProjectModelAwaitedRuns(this IServiceCollection services) =>
        services.AddSingleton(new PipelineHolder())
                .AddSingleton<RunsState>()
                .AddHostedService<RunsWorker>();
}
