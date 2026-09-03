using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public static class FixtureTableExtensions
{
    public static EntityTypeBuilder<TEntity> ToTable<TEntity>(this EntityTypeBuilder<TEntity> builder,
                                                                string name, string schema)
        where TEntity : class => builder;
}

[Table("attribute_rows", Schema = "catalog")]
public sealed class AttributeEntity
{
    public int Id { get; set; }
}

public sealed class FluentEntity
{
    public int Id { get; set; }
}

public sealed class ConfiguredEntity
{
    public int Id { get; set; }
}

public sealed class ConventionEntity
{
    public int Id { get; set; }
}

public sealed class SharedEntity
{
    public int Id { get; set; }
}

public sealed class FixtureDbContext : DbContext
{
    public DbSet<AttributeEntity> AttributeEntities { get; set; } = null!;
    public DbSet<FluentEntity> FluentEntities { get; set; } = null!;
    public DbSet<ConfiguredEntity> ConfiguredEntities { get; set; } = null!;
    public DbSet<ConventionEntity> ConventionRows { get; set; } = null!;
    public DbSet<SharedEntity> SharedRows { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AttributeEntity>().ToTable("ignored_by_attribute", "ignored");
        modelBuilder.Entity<FluentEntity>().ToTable("fluent_rows", "inventory");
    }
}

public sealed class ConfiguredEntityConfiguration : IEntityTypeConfiguration<ConfiguredEntity>
{
    public void Configure(EntityTypeBuilder<ConfiguredEntity> builder)
    {
        builder.ToTable("configured_rows", "custom");
    }
}

public sealed class EfReadsController : ControllerBase
{
    private readonly FixtureDbContext _database = null!;

    public object AttributeRead() => _database.AttributeEntities.Where(entity => entity.Id > 0).ToList();

    public object FluentSetRead() => _database.Set<FluentEntity>().Where(entity => entity.Id > 0).ToList();

    public object ConfigurationSetRead() => _database.Set<ConfiguredEntity>().ToList();

    public object ConventionRead() => _database.ConventionRows.Select(entity => entity.Id).ToList();

    public object SharedRead() => _database.SharedRows.ToList();
}
