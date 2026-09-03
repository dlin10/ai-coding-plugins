using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace CorpusCatalogue;

[Table("Products", Schema = "dbo")]
public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

[Table("Discounts", Schema = "dbo")]
public sealed class Discount
{
    public int Id { get; set; }
}

[Table("BudgetEntries", Schema = "dbo")]
public sealed class BudgetEntry
{
    public int Id { get; set; }
}

public sealed class CatalogueDbContext : DbContext
{
    public DbSet<Product> Products { get; set; } = null!;
    public DbSet<Discount> Discounts { get; set; } = null!;
    public DbSet<BudgetEntry> BudgetEntries { get; set; } = null!;
}

public sealed class CatalogueController : ControllerBase
{
    private readonly IMemoryCache _cache = null!;
    private readonly IDatabase _redis = null!;
    private readonly CatalogueDbContext _database = null!;

    public object GetProduct(int id)
    {
        var product = _database.Products.First();
        var discounts = _database.Discounts.ToList();
        var result = new { product, discounts };
        _cache.Set("product:" + id, result);
        return result;
    }

    public void UpdateProduct(int id)
    {
        _database.Products.Update(new Product { Id = id, Name = "updated" });
        _database.SaveChanges();
        _cache.Remove("product:" + id);
    }

    public void RemoveTypo(int id) => _cache.Remove("products:" + id);

    public void RemoveLegacy(int id) => _cache.Remove("legacy:" + id);

    public void SetSession(int id) => _cache.Set("session:" + id, "state");

    public void AcquireLock(int id) =>
        _redis.StringSet("lock:" + id, "held", when: When.NotExists);

    public object GetBudgeted(int id)
    {
        var value = _database.BudgetEntries.First();
        _cache.Set("budget:" + id, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
        });
        return value;
    }

    public void UpdateBudgeted(int id)
    {
        _database.BudgetEntries.Update(new BudgetEntry { Id = id });
        _database.SaveChanges();
    }
}
