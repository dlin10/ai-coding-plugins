using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.AmbiguousRegistrationScopedWins;

public sealed class CartDraft
{
    public string? Item { get; set; }
}

/// <summary>The last registration wins, so each request gets its own scoped cart draft.</summary>
[ApiController]
[Route("cases/ambiguous-registration-scoped-wins")]
public sealed class CartDraftController : ControllerBase
{
    private readonly CartDraft _draft;

    public CartDraftController(CartDraft draft) => _draft = draft;

    [HttpPost]
    public void Post(string item) => _draft.Item = item;
}

public static class AmbiguousRegistrationScopedWinsCase
{
    public static IServiceCollection AddAmbiguousRegistrationScopedWins(this IServiceCollection services) =>
        services.AddSingleton<CartDraft>()
                .AddScoped<CartDraft>();
}
