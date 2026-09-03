using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

public static class FixtureExecuteExtensions
{
    public static int ExecuteUpdate<TEntity>(this IQueryable<TEntity> source) where TEntity : class => 0;

    public static Task<int> ExecuteUpdateAsync<TEntity>(this IQueryable<TEntity> source,
                                                         CancellationToken cancellationToken = default)
        where TEntity : class => Task.FromResult(0);

    public static int ExecuteDelete<TEntity>(this IQueryable<TEntity> source) where TEntity : class => 0;

    public static Task<int> ExecuteDeleteAsync<TEntity>(this IQueryable<TEntity> source,
                                                         CancellationToken cancellationToken = default)
        where TEntity : class => Task.FromResult(0);
}

public abstract class MutableEntity
{
    public int Id { get; set; }
    public int Value { get; set; }
}

public sealed class AddEntity : MutableEntity { }
public sealed class AddRangeEntity : MutableEntity { }
public sealed class UpdateEntity : MutableEntity { }
public sealed class UpdateRangeEntity : MutableEntity { }
public sealed class RemoveEntity : MutableEntity { }
public sealed class RemoveRangeEntity : MutableEntity { }
public sealed class AttachEntity : MutableEntity { }
public sealed class AssignedEntity : MutableEntity { }
public sealed class FindEntity : MutableEntity { }
public sealed class EntryEntity : MutableEntity { }
public sealed class ExecuteUpdateEntity : MutableEntity { }
public sealed class ExecuteUpdateAsyncEntity : MutableEntity { }
public sealed class ExecuteDeleteEntity : MutableEntity { }
public sealed class ExecuteDeleteAsyncEntity : MutableEntity { }
public sealed class LikelyEntity : MutableEntity
{
    public void Touch() { }
}
public sealed class HelperEntity : MutableEntity { }
public sealed class CrossMethodEntity : MutableEntity { }
public sealed class AsyncSaveEntity : MutableEntity { }

public sealed class OrderEntity : MutableEntity
{
    public ICollection<OrderLineEntity> Lines { get; } = new List<OrderLineEntity>();
}

public sealed class OrderLineEntity : MutableEntity { }

public sealed class WritesDbContext : DbContext
{
    public DbSet<AddEntity> AddRows { get; set; } = null!;
    public DbSet<AddRangeEntity> AddRangeRows { get; set; } = null!;
    public DbSet<UpdateEntity> UpdateRows { get; set; } = null!;
    public DbSet<UpdateRangeEntity> UpdateRangeRows { get; set; } = null!;
    public DbSet<RemoveEntity> RemoveRows { get; set; } = null!;
    public DbSet<RemoveRangeEntity> RemoveRangeRows { get; set; } = null!;
    public DbSet<AttachEntity> AttachRows { get; set; } = null!;
    public DbSet<AssignedEntity> AssignedRows { get; set; } = null!;
    public DbSet<FindEntity> FindRows { get; set; } = null!;
    public DbSet<EntryEntity> EntryRows { get; set; } = null!;
    public DbSet<ExecuteUpdateEntity> ExecuteUpdateRows { get; set; } = null!;
    public DbSet<ExecuteUpdateAsyncEntity> ExecuteUpdateAsyncRows { get; set; } = null!;
    public DbSet<ExecuteDeleteEntity> ExecuteDeleteRows { get; set; } = null!;
    public DbSet<ExecuteDeleteAsyncEntity> ExecuteDeleteAsyncRows { get; set; } = null!;
    public DbSet<LikelyEntity> LikelyRows { get; set; } = null!;
    public DbSet<HelperEntity> HelperRows { get; set; } = null!;
    public DbSet<CrossMethodEntity> CrossMethodRows { get; set; } = null!;
    public DbSet<AsyncSaveEntity> AsyncSaveRows { get; set; } = null!;
    public DbSet<OrderEntity> Orders { get; set; } = null!;
    public DbSet<OrderLineEntity> OrderLines { get; set; } = null!;
}

public sealed class EfWritesController : ControllerBase
{
    private readonly WritesDbContext _database = null!;

    public void Add()
    {
        _database.AddRows.Add(new AddEntity());
        _database.SaveChanges();
    }

    public void AddRange()
    {
        _database.AddRangeRows.AddRange(new AddRangeEntity());
        _database.SaveChanges();
    }

    public void Update()
    {
        _database.UpdateRows.Update(new UpdateEntity());
        _database.SaveChanges();
    }

    public void UpdateRange()
    {
        _database.UpdateRangeRows.UpdateRange(new UpdateRangeEntity());
        _database.SaveChanges();
    }

    public void Remove()
    {
        _database.RemoveRows.Remove(new RemoveEntity());
        _database.SaveChanges();
    }

    public void RemoveRange()
    {
        _database.RemoveRangeRows.RemoveRange(new RemoveRangeEntity());
        _database.SaveChanges();
    }

    public void AttachThenAssign()
    {
        var entity = new AttachEntity();
        _database.AttachRows.Attach(entity);
        entity.Value = 1;
        _database.SaveChanges();
    }

    public void AssignQueriedEntity()
    {
        var entity = _database.AssignedRows.First();
        entity.Value = 1;
        _database.SaveChanges();
    }

    public void AssignFoundEntity()
    {
        var entity = _database.Find<FindEntity>(1)!;
        entity.Value = 1;
        _database.SaveChanges();
    }

    public void SetEntryState()
    {
        var entity = new EntryEntity();
        _database.Entry(entity).State = EntityState.Modified;
        _database.SaveChanges();
    }

    public int ExecuteUpdateOnly() => _database.ExecuteUpdateRows.ExecuteUpdate();

    public Task<int> ExecuteUpdateAsyncOnly() => _database.ExecuteUpdateAsyncRows.ExecuteUpdateAsync();

    public int ExecuteDeleteOnly() => _database.ExecuteDeleteRows.ExecuteDelete();

    public Task<int> ExecuteDeleteAsyncOnly() => _database.ExecuteDeleteAsyncRows.ExecuteDeleteAsync();

    public void AddNavigationChild()
    {
        var order = _database.Orders.First();
        order.Lines.Add(new OrderLineEntity());
        _database.SaveChanges();
    }

    public void LikelyMutation()
    {
        var entity = _database.LikelyRows.First();
        entity.Touch();
        _database.SaveChanges();
    }

    public void ReachesMutationAndSave() => MutateAndSave();

    public void MutationThenReachedSave()
    {
        _database.CrossMethodRows.Add(new CrossMethodEntity());
        SaveOnly();
    }

    public async Task SaveChangesAsyncWrite()
    {
        _database.AsyncSaveRows.Add(new AsyncSaveEntity());
        await _database.SaveChangesAsync();
    }

    private void MutateAndSave()
    {
        _database.HelperRows.Add(new HelperEntity());
        _database.SaveChanges();
    }

    private void SaveOnly() => _database.SaveChanges();
}
