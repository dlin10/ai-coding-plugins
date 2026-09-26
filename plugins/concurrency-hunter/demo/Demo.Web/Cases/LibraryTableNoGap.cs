using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LibraryTableNoGap;

/// <summary>A singleton only read, handed to a logger, the serializer and an HTTP client: the library table describes each of those
/// calls, so none of them is a semantic gap however much the singleton reaches (TD-034a).</summary>
public sealed class Ledger
{
    public int Total;
    public readonly HttpRequestMessage Request = new(HttpMethod.Get, "http://localhost/ledger");
}

[ApiController]
[Route("cases/library-table-no-gap")]
public sealed class LedgerController : ControllerBase
{
    private readonly Ledger _ledger;
    private readonly ILogger<LedgerController> _logger;

    public LedgerController(Ledger ledger, ILogger<LedgerController> logger)
    {
        _ledger = ledger;
        _logger = logger;
    }

    [HttpGet]
    public async Task<string> Get()
    {
        _logger.LogInformation("Ledger {Ledger}", _ledger);
        using var client = new HttpClient();
        using var response = await client.SendAsync(_ledger.Request);
        return JsonSerializer.Serialize(_ledger);
    }
}

public static class LibraryTableNoGapCase
{
    public static IServiceCollection AddLibraryTableNoGap(this IServiceCollection services) =>
        services.AddSingleton<Ledger>();
}
