using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LockHeldByCaller;

/// <summary>Unsafe on its own; every caller in this program holds the lock.</summary>
public sealed class Tokens
{
    private int _issued;

    public void Issue() => _issued++;
}

/// <summary>Hard negative: the protection is acquired in the root and must reach the callee's access
/// through the summary instantiation.</summary>
[ApiController]
[Route("cases/lock-held-by-caller")]
public sealed class TokensController : ControllerBase
{
    private readonly Tokens _tokens;

    public TokensController(Tokens tokens) => _tokens = tokens;

    [HttpPost]
    public void Post()
    {
        lock (_tokens)
        {
            _tokens.Issue();
        }
    }
}

public static class LockHeldByCallerCase
{
    public static IServiceCollection AddLockHeldByCaller(this IServiceCollection services) =>
        services.AddSingleton<Tokens>();
}
