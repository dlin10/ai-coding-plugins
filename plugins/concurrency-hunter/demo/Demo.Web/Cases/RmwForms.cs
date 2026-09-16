using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.RmwForms;

/// <summary>Read-modify-write in four forms, one field each: a compound assignment, a property read and set
/// back, a read into a local written over several statements, and a read and a write through two private
/// methods.</summary>
public sealed class Tallies
{
    private int _compound;
    private int _split;
    private int _level;

    public int Views { get; set; }

    public void AddCompound(int amount) => _compound += amount;

    public void AddView() => Views = Views + 1;

    public void AddSplit(int amount)
    {
        var current = _split;
        var next = current + amount;
        _split = next;
    }

    public void RaiseLevel() => SetLevel(GetLevel() + 1);

    private int GetLevel() => _level;

    private void SetLevel(int level) => _level = level;
}

[ApiController]
[Route("cases/rmw-forms")]
public sealed class TalliesController : ControllerBase
{
    private readonly Tallies _tallies;

    public TalliesController(Tallies tallies) => _tallies = tallies;

    [HttpPost("compound")]
    public void Compound(int amount) => _tallies.AddCompound(amount);

    [HttpPost("property")]
    public void Property() => _tallies.AddView();

    [HttpPost("split")]
    public void Split(int amount) => _tallies.AddSplit(amount);

    [HttpPost("through-methods")]
    public void ThroughMethods() => _tallies.RaiseLevel();
}

public static class RmwFormsCase
{
    public static IServiceCollection AddRmwForms(this IServiceCollection services) =>
        services.AddSingleton<Tallies>();
}
