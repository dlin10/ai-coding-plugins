using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ConcurrentBagCountThenAdd;

/// <summary>A limit read from the bag decides whether to add to it. Both calls are atomic and the sequence is not, so two
/// requests can pass the same check and the bag grows past the limit. The dependency runs from a count to an addition, and only
/// the structure holds both, so this is where the sequence is reported (ADR 0010).</summary>
public sealed class Intake
{
    private const int Limit = 4;

    public ConcurrentBag<string> Items { get; } = new();

    public void Accept(string item)
    {
        if (Items.Count < Limit)
            Items.Add(item);
    }
}

[ApiController]
[Route("cases/concurrent-bag-count-then-add")]
public sealed class IntakeController : ControllerBase
{
    private readonly Intake _intake;

    public IntakeController(Intake intake) => _intake = intake;

    [HttpPost]
    public void Post(string item) => _intake.Accept(item);
}

public static class ConcurrentBagCountThenAddCase
{
    public static IServiceCollection AddConcurrentBagCountThenAdd(this IServiceCollection services) =>
        services.AddSingleton<Intake>();
}
