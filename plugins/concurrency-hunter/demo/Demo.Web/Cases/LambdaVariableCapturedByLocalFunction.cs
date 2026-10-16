using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LambdaVariableCapturedByLocalFunction;

public sealed class Tally
{
    public int Count;
}

/// <summary>A lambda declares a variable and a local function it calls directly reaches it: the variable lives in the closure each
/// run of the lambda creates. What the variable holds is reached through that cell, so every request writes the one singleton;
/// and the lambda's own write of the cell is an access on it, which meets the increment of the task it started before awaiting.</summary>
[ApiController]
[Route("cases/lambda-variable-captured-by-local-function")]
public sealed class TallyController : ControllerBase
{
    private readonly Tally _shared;

    public TallyController(Tally shared) => _shared = shared;

    [HttpPost]
    public void Post()
    {
        Action record = () =>
        {
            var tally = _shared;
            Bump();

            void Bump() => tally.Count++;
        };
        record();
    }

    [HttpPost("cell")]
    public async Task Mark()
    {
        Func<Task> record = async () =>
        {
            var marks = 0;
            var task = Task.Run(() => Bump());
            marks = 5;
            await task;

            void Bump() => marks++;
        };
        await record();
    }
}

public static class LambdaVariableCapturedByLocalFunctionCase
{
    public static IServiceCollection AddLambdaVariableCapturedByLocalFunction(this IServiceCollection services) =>
        services.AddSingleton<Tally>();
}
