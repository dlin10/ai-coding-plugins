using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CorpusPricing;

[Table("Discounts", Schema = "dbo")]
public sealed class Discount
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
}

public sealed class PricingDbContext : DbContext
{
    public DbSet<Discount> Discounts { get; set; } = null!;
}

public sealed class PricingController : ControllerBase
{
    private readonly PricingDbContext _database = null!;

    public void UpdateDiscount(int id)
    {
        _database.Discounts.Update(new Discount { Id = id, Amount = 10 });
        _database.SaveChanges();
    }
}
