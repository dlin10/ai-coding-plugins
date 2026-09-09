using Contracts;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Http.Json;

namespace Notifications;

public sealed class PriceChangedConsumer(IMemoryCache memory) : IConsumer<PriceChanged>
{
    /// <summary>Case H consumer: its invalidation covers the Pricing write through a confirmed event hop.</summary>
    public Task Consume(ConsumeContext<PriceChanged> context)
    {
        memory.Remove($"price:{context.Message.Id}");
        return Task.CompletedTask;
    }
}

public sealed class ProductRenamedConsumer : IConsumer<ProductRenamed>
{
    /// <summary>Case I consumer: it observes the event but deliberately leaves product cache stale.</summary>
    public Task Consume(ConsumeContext<ProductRenamed> context)
    {
        Console.WriteLine(context.Message.Id);
        return Task.CompletedTask;
    }
}

public interface ICatalogClient
{
    Task<int> GetInventory(int id);
}

public sealed class CatalogClient(HttpClient client) : ICatalogClient
{
    public Task<int> GetInventory(int id) => client.GetFromJsonAsync<int>($"products/{id}/inventory")!;
}

[ApiController]
[Route("digest")]
public sealed class DigestController(ICatalogClient catalog, IMemoryCache memory) : ControllerBase
{
    /// <summary>Case J: Catalog inventory is cached through a typed HTTP client without a TTL.</summary>
    [HttpGet("{id:int}")]
    public async Task<int> GetDigest(int id)
    {
        if (memory.TryGetValue<int>($"digest:{id}", out var cached)) return cached;
        var digest = await catalog.GetInventory(id);
        memory.Set($"digest:{id}", digest);
        return digest;
    }
}

[ApiController]
[Route("weather")]
public sealed class WeatherController(HttpClient client, IMemoryCache memory) : ControllerBase
{
    /// <summary>Case K: an external HTTP response is cached with no TTL.</summary>
    [HttpGet]
    public async Task<string> Get()
    {
        if (memory.TryGetValue<string>("weather:today", out var cached)) return cached!;
        var weather = await client.GetStringAsync("https://api.weather.invalid/today");
        memory.Set("weather:today", weather);
        return weather;
    }
}
