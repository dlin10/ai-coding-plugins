using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Catalog;

public sealed class ShopContext : DbContext
{
    public ShopContext(DbContextOptions<ShopContext> options)
        : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();

    /// <summary>Mapped to a view. Nothing in the code says so — which is the point: the scanner meets it
    /// as an ordinary table and only the catalogue can tell it is <c>vw_ProductCard</c>.</summary>
    public DbSet<ProductCard> ProductCards => Set<ProductCard>();

    public DbSet<Price> Prices => Set<Price>();

    public DbSet<InventoryLevel> Inventory => Set<InventoryLevel>();
}

[Table("Products")]
public sealed class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

[Table("vw_ProductCard")]
public sealed class ProductCard
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal LatestPrice { get; set; }
}

[Table("Prices")]
public sealed class Price
{
    public int ProductId { get; set; }

    public decimal Amount { get; set; }
}

[Table("Inventory")]
public sealed class InventoryLevel
{
    public int ProductId { get; set; }

    public int OnHand { get; set; }
}
