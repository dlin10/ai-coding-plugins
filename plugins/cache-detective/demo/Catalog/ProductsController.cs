using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using MassTransit;
using Contracts;

namespace Catalog;

/// <summary>
/// The caching half of the demo. Every action below names the outcome the scanner should report for it.
/// The outcomes assume both halves are indexed — <c>index_solution</c> for this code and
/// <c>index_database</c> for <c>db/shop.sql</c> — because a chain through a stored procedure is only
/// complete once the catalogue has been read.
/// </summary>
[ApiController]
[Route("products")]
public sealed class ProductsController : ControllerBase
{
    private readonly ShopContext _context;
    private readonly IMemoryCache _memory;
    private readonly IDatabase _redis;
    private readonly IPublishEndpoint _publishEndpoint;

    public ProductsController(ShopContext context, IMemoryCache memory, IDatabase redis, IPublishEndpoint publishEndpoint)
    {
        _context = context;
        _memory = memory;
        _redis = redis;
        _publishEndpoint = publishEndpoint;
    }

    /// <summary>
    /// Case A, the caching end. Reads the product card — which is the view <c>vw_ProductCard</c> over
    /// <c>dbo.PriceHistory</c> — and caches it with no expiry and no invalidation anywhere.
    /// <para>Expected: one UNGUARDED_WRITE finding, confidence <c>confirmed</c>, whose subject is the
    /// Pricing handler that calls <c>dbo.ApplyDiscount</c>. The write itself is three hops away: the
    /// procedure writes <c>dbo.Discounts</c>, the trigger on that table writes <c>dbo.PriceHistory</c>,
    /// and this view reads it.</para>
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<ProductCard?> GetProduct(int id)
    {
        if (_memory.TryGetValue<ProductCard>($"product:{id}", out var cached))
        {
            return cached;
        }

        var card = await _context.ProductCards.FirstOrDefaultAsync(candidate => candidate.Id == id);
        _memory.Set($"product:{id}", card);
        return card;
    }

    /// <summary>
    /// Case F, the caching end. The key this caches is invalidated by the very Pricing handler whose
    /// procedure writes <c>dbo.Prices</c>.
    /// <para>Expected: no finding. This is the anchor check — the write is hidden inside a procedure, so
    /// a rule that looked for the invalidation around the <em>writer</em> rather than around the handler
    /// at the head of the chain would report it, wrongly.</para>
    /// </summary>
    [HttpGet("{id:int}/price")]
    public async Task<decimal> GetPrice(int id)
    {
        if (_memory.TryGetValue<decimal>($"price:{id}", out var cached))
        {
            return cached;
        }

        var amount = await _context.Prices.Where(price => price.ProductId == id)
                                          .Select(price => price.Amount)
                                          .FirstOrDefaultAsync();
        _memory.Set($"price:{id}", amount, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        });
        return amount;
    }

    /// <summary>
    /// Case G, the caching end. Thirty seconds of staleness, inside the default sixty-second budget.
    /// <para>Expected: no finding reported, even though <see cref="AdjustInventory"/> writes the table
    /// and nothing invalidates the key — the finding exists but is suppressed by the budget.</para>
    /// </summary>
    [HttpGet("{id:int}/inventory")]
    public async Task<int> GetInventory(int id)
    {
        if (_memory.TryGetValue<int>($"inventory:{id}", out var cached))
        {
            return cached;
        }

        var onHand = await _context.Inventory.Where(level => level.ProductId == id)
                                             .Select(level => level.OnHand)
                                             .FirstOrDefaultAsync();
        _memory.Set($"inventory:{id}", onHand, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
        });
        return onHand;
    }

    /// <summary>Case G, the writing end: a plain EF write with no invalidation.</summary>
    [HttpPost("{id:int}/inventory")]
    public async Task AdjustInventory(int id, int delta)
    {
        var level = await _context.Inventory.FirstAsync(candidate => candidate.ProductId == id);
        level.OnHand += delta;
        await _context.SaveChangesAsync();
    }

    /// <summary>Case I: the event leaves this service, but Notifications does not invalidate product cards.
    /// Expected: CROSS_SERVICE_GAP for <c>product:{id}</c>.</summary>
    [HttpPost("{id:int}/rename")]
    public async Task Rename(int id, string name)
    {
        var product = await _context.Products.FirstAsync(candidate => candidate.Id == id);
        product.Name = name;
        await _context.SaveChangesAsync();
        await _publishEndpoint.Publish(new ProductRenamed(id));
    }

    /// <summary>
    /// Not one of the seven cases: a Redis key that is storage rather than a cache.
    /// <para>Expected: role <c>store</c>, and therefore no finding of any kind — the detection rules only
    /// look at keys whose role is <c>cache</c>.</para>
    /// </summary>
    [HttpPut("session/{userId:int}")]
    public async Task StoreSession(int userId, string token)
    {
        await _redis.StringSetAsync($"session:{userId}", token, TimeSpan.FromHours(8));
    }
}
