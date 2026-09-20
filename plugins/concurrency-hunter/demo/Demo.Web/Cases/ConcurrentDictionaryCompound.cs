using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ConcurrentDictionaryCompound;

/// <summary>Each step of these two sequences is atomic, and each sequence is not: between the check and the change another
/// request runs the same sequence, so the second change is made as if the first had never happened. A thread-safe collection
/// answers neither, which is what makes them compound operations rather than ordinary conflicts (ADR 0010).</summary>
public sealed class Registry
{
    public ConcurrentDictionary<string, int> Flags { get; } = new();

    public ConcurrentDictionary<string, int> Counts { get; } = new();
}

[ApiController]
[Route("cases/concurrent-dictionary-compound")]
public sealed class RegistryController : ControllerBase
{
    private readonly Registry _registry;

    public RegistryController(Registry registry) => _registry = registry;

    [HttpPost("flag")]
    public void PostFlag()
    {
        if (!_registry.Flags.ContainsKey("ready"))
            _registry.Flags["ready"] = 1;
    }

    [HttpPost("count")]
    public void PostCount() => _registry.Counts["hits"] = _registry.Counts["hits"] + 1;
}

public static class ConcurrentDictionaryCompoundCase
{
    public static IServiceCollection AddConcurrentDictionaryCompound(this IServiceCollection services) =>
        services.AddSingleton<Registry>();
}
