using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.SameLockViaField;

/// <summary>Hard negative: the counter update is under a lock whose identity is a readonly field of the
/// same singleton, one call below the root.</summary>
public sealed class Sequence
{
    private readonly object _gate = new();
    private int _next;

    public void Advance()
    {
        lock (_gate)
        {
            _next++;
        }
    }
}

[ApiController]
[Route("cases/same-lock-via-field")]
public sealed class SequenceController : ControllerBase
{
    private readonly Sequence _sequence;

    public SequenceController(Sequence sequence) => _sequence = sequence;

    [HttpPost]
    public void Post() => _sequence.Advance();
}

public static class SameLockViaFieldCase
{
    public static IServiceCollection AddSameLockViaField(this IServiceCollection services) =>
        services.AddSingleton<Sequence>();
}
