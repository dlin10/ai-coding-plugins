using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.DiFactoryRegistration;

public sealed class RateCard
{
    public decimal Rate { get; set; }
}

/// <summary>A singleton registered through a factory lambda is still one shared instance: the PUT races
/// with itself.</summary>
[ApiController]
[Route("cases/di-factory-registration")]
public sealed class RateCardController : ControllerBase
{
    private readonly RateCard _card;

    public RateCardController(RateCard card) => _card = card;

    [HttpPut]
    public void Put(decimal rate) => _card.Rate = rate;
}

public static class DiFactoryRegistrationCase
{
    public static IServiceCollection AddDiFactoryRegistration(this IServiceCollection services) =>
        services.AddSingleton(_ => new RateCard());
}
