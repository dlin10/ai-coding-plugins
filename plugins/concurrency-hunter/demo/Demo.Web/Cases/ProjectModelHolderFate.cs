using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ProjectModelHolderFate;

/// <summary>An object the registry compares with itself.</summary>
public sealed class Tag
{
}

/// <summary>A singleton keeping an equality comparer whose equality counts its runs. No built-in model describes
/// <c>EqualityComparer&lt;T&gt;.Create</c>: the project model in <c>.concurrency-hunter/models/demo.json</c> says the comparer holds
/// both delegates, so they run wherever a member of the comparer is called.</summary>
public sealed class Registry
{
    public int Hits;
    public Tag First;
    public EqualityComparer<Tag> Same;

    public Registry()
    {
        First = new Tag();
        Same = EqualityComparer<Tag>.Create((a, b) => { Hits++; return ReferenceEquals(a, b); }, t => 0);
    }
}

/// <summary>Compares through the singleton's comparer, which runs its equality in this action's execution.</summary>
[ApiController]
[Route("cases/project-model-holder-fate")]
public sealed class RegistryController : ControllerBase
{
    private readonly Registry _registry;

    public RegistryController(Registry registry) => _registry = registry;

    [HttpPost]
    public bool Post() => _registry.Same.Equals(_registry.First, _registry.First);
}

public static class ProjectModelHolderFateCase
{
    public static IServiceCollection AddProjectModelHolderFate(this IServiceCollection services) =>
        services.AddSingleton<Registry>();
}
