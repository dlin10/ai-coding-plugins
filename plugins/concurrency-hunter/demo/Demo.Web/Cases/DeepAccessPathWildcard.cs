using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.DeepAccessPathWildcard;

public sealed class Level12
{
    public string? Value { get; set; }
}

public sealed class Level11
{
    public Level12 Next { get; } = new();
}

public sealed class Level10
{
    public Level11 Next { get; } = new();
}

public sealed class Level9
{
    public Level10 Next { get; } = new();
}

public sealed class Level8
{
    public Level9 Next { get; } = new();
}

public sealed class Level7
{
    public Level8 Next { get; } = new();
}

public sealed class Level6
{
    public Level7 Next { get; } = new();
}

public sealed class Level5
{
    public Level6 Next { get; } = new();
}

public sealed class Level4
{
    public Level5 Next { get; } = new();
}

public sealed class Level3
{
    public Level4 Next { get; } = new();
}

public sealed class Level2
{
    public Level3 Next { get; } = new();
}

public sealed class Level1
{
    public Level2 Next { get; } = new();
}

/// <summary>A write through a field chain of 13 segments (First, eleven Next, Value), deeper than the access
/// path limit, collapses to a wildcard path with a lowered confidence; the expectations hold for any depth
/// limit from 1 to 11.</summary>
public sealed class DeepChain
{
    public Level1 First { get; } = new();

    public void Write(string value) => First.Next.Next.Next.Next.Next.Next.Next.Next.Next.Next.Next.Value = value;
}

[ApiController]
[Route("cases/deep-access-path-wildcard")]
public sealed class DeepChainController : ControllerBase
{
    private readonly DeepChain _chain;

    public DeepChainController(DeepChain chain) => _chain = chain;

    [HttpPut]
    public void Put(string value) => _chain.Write(value);
}

public static class DeepAccessPathWildcardCase
{
    public static IServiceCollection AddDeepAccessPathWildcard(this IServiceCollection services) =>
        services.AddSingleton<DeepChain>();
}
