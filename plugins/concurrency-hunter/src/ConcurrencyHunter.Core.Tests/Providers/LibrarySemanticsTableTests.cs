using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibrarySemantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The contract tests of the library semantics table (R4): the table against the real metadata of the shared runtime,
/// its reference pack and the packages this project references, and the self-check its construction runs.</summary>
public sealed class LibrarySemanticsTableTests
{
    private static readonly LibrarySemanticsTable Table = LibrarySemanticsTable.BuiltIn;

    private static readonly string RuntimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    // The real packages are never compiled beside a stub of the same name: the stub only ever joins the runtime alone.
    private static readonly Lazy<CSharpCompilation> Real = new(() => Compile("", StubAssemblies.PlatformWithout([]).Concat(LibraryPackages.All)));

    /// <summary>The phase 5a list (R2), read off "Состав таблицы" against the real metadata: every member by its id with its effects,
    /// and every immutable type, the one that covers its derived types marked so.</summary>
    private static readonly (string Id, string Effects)[] Phase5aList =
    [
        ("M:Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker.Entries``1~System.Collections.Generic.IEnumerable{Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry{``0}}", ""),
        ("M:Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry`1.Collection``1(System.Linq.Expressions.Expression{System.Func{`0,System.Collections.Generic.IEnumerable{``0}}})~Microsoft.EntityFrameworkCore.ChangeTracking.CollectionEntry{`0,``0}", ""),
        ("M:Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry`1.Reference``1(System.Linq.Expressions.Expression{System.Func{`0,``0}})~Microsoft.EntityFrameworkCore.ChangeTracking.ReferenceEntry{`0,``0}", ""),
        ("M:Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry`1.get_Entity~`0", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.#ctor", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.#ctor(Microsoft.EntityFrameworkCore.DbContextOptions)", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.Add(System.Object)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.AddAsync(System.Object,System.Threading.CancellationToken)~System.Threading.Tasks.ValueTask{Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry}", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.AddAsync``1(``0,System.Threading.CancellationToken)~System.Threading.Tasks.ValueTask{Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry{``0}}", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.AddRange(System.Collections.Generic.IEnumerable{System.Object})", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.AddRange(System.Object[])", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.AddRangeAsync(System.Collections.Generic.IEnumerable{System.Object},System.Threading.CancellationToken)~System.Threading.Tasks.Task", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.AddRangeAsync(System.Object[])~System.Threading.Tasks.Task", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.Add``1(``0)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry{``0}", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.Attach(System.Object)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.AttachRange(System.Collections.Generic.IEnumerable{System.Object})", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.AttachRange(System.Object[])", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.Attach``1(``0)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry{``0}", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.Dispose", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.DisposeAsync~System.Threading.Tasks.ValueTask", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.Entry(System.Object)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.Entry``1(``0)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry{``0}", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.Find(System.Type,System.Object[])~System.Object", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.FindAsync(System.Type,System.Object[])~System.Threading.Tasks.ValueTask{System.Object}", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.FindAsync(System.Type,System.Object[],System.Threading.CancellationToken)~System.Threading.Tasks.ValueTask{System.Object}", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.FindAsync``1(System.Object[])~System.Threading.Tasks.ValueTask{``0}", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.FindAsync``1(System.Object[],System.Threading.CancellationToken)~System.Threading.Tasks.ValueTask{``0}", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.Find``1(System.Object[])~``0", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.Remove(System.Object)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.RemoveRange(System.Collections.Generic.IEnumerable{System.Object})", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.RemoveRange(System.Object[])", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.Remove``1(``0)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry{``0}", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.SaveChanges(System.Boolean)~System.Int32", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(System.Boolean,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Int32}", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Int32}", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.SaveChanges~System.Int32", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.Update(System.Object)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.UpdateRange(System.Collections.Generic.IEnumerable{System.Object})", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.UpdateRange(System.Object[])", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.Update``1(``0)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry{``0}", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbContext.get_ChangeTracker~Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker", ""),
        ("M:Microsoft.EntityFrameworkCore.DbContext.get_Database~Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade", ""),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.Add(`0)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry{`0}", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.AddAsync(`0,System.Threading.CancellationToken)~System.Threading.Tasks.ValueTask{Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry{`0}}", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.AddRange(System.Collections.Generic.IEnumerable{`0})", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.AddRange(`0[])", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.AddRangeAsync(System.Collections.Generic.IEnumerable{`0},System.Threading.CancellationToken)~System.Threading.Tasks.Task", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.AddRangeAsync(`0[])~System.Threading.Tasks.Task", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.Attach(`0)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry{`0}", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.AttachRange(System.Collections.Generic.IEnumerable{`0})", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.AttachRange(`0[])", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.Entry(`0)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry{`0}", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.Find(System.Object[])~`0", ""),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.FindAsync(System.Object[])~System.Threading.Tasks.ValueTask{`0}", ""),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.FindAsync(System.Object[],System.Threading.CancellationToken)~System.Threading.Tasks.ValueTask{`0}", ""),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.Remove(`0)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry{`0}", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.RemoveRange(System.Collections.Generic.IEnumerable{`0})", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.RemoveRange(`0[])", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.Update(`0)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry{`0}", "DeepRead:entity,WriteArgument:entity"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.UpdateRange(System.Collections.Generic.IEnumerable{`0})", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.UpdateRange(`0[])", "DeepRead:entities,WriteArgument:entities"),
        ("M:Microsoft.EntityFrameworkCore.DbSet`1.get_Local~Microsoft.EntityFrameworkCore.ChangeTracking.LocalView{`0}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}},System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Boolean}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync``1(System.Linq.IQueryable{``0},System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Boolean}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}},System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Int32}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync``1(System.Linq.IQueryable{``0},System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Int32}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}},System.Threading.CancellationToken)~System.Threading.Tasks.Task{``0}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync``1(System.Linq.IQueryable{``0},System.Threading.CancellationToken)~System.Threading.Tasks.Task{``0}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}},System.Threading.CancellationToken)~System.Threading.Tasks.Task{``0}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync``1(System.Linq.IQueryable{``0},System.Threading.CancellationToken)~System.Threading.Tasks.Task{``0}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include``1(System.Linq.IQueryable{``0},System.String)~System.Linq.IQueryable{``0}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include``2(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,``1}})~Microsoft.EntityFrameworkCore.Query.IIncludableQueryable{``0,``1}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.LongCountAsync``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}},System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Int64}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.LongCountAsync``1(System.Linq.IQueryable{``0},System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Int64}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}},System.Threading.CancellationToken)~System.Threading.Tasks.Task{``0}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync``1(System.Linq.IQueryable{``0},System.Threading.CancellationToken)~System.Threading.Tasks.Task{``0}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleOrDefaultAsync``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}},System.Threading.CancellationToken)~System.Threading.Tasks.Task{``0}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleOrDefaultAsync``1(System.Linq.IQueryable{``0},System.Threading.CancellationToken)~System.Threading.Tasks.Task{``0}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToArrayAsync``1(System.Linq.IQueryable{``0},System.Threading.CancellationToken)~System.Threading.Tasks.Task{``0[]}", ""),
        ("M:Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync``1(System.Linq.IQueryable{``0},System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Collections.Generic.List{``0}}", ""),
        ("M:Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.BeginTransactionAsync(System.Threading.CancellationToken)~System.Threading.Tasks.Task{Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction}", ""),
        ("M:Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.BeginTransaction~Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction", ""),
        ("M:Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.CreateExecutionStrategy~Microsoft.EntityFrameworkCore.Storage.IExecutionStrategy", ""),
        ("M:Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade.get_CurrentTransaction~Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction", ""),
        ("M:Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.BeginTransactionAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade,System.Data.IsolationLevel,System.Threading.CancellationToken)~System.Threading.Tasks.Task{Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction}", ""),
        ("M:Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade)~System.Data.Common.DbConnection", ""),
        ("M:Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.Migrate(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade)", ""),
        ("M:Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.Migrate(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade,System.String)", ""),
        ("M:Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade,System.String,System.Threading.CancellationToken)~System.Threading.Tasks.Task", ""),
        ("M:Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade,System.Threading.CancellationToken)~System.Threading.Tasks.Task", ""),
        ("M:Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.UseTransaction(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade,System.Data.Common.DbTransaction)~Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction", ""),
        ("M:Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.UseTransaction(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade,System.Data.Common.DbTransaction,System.Guid)~Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction", ""),
        ("M:Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction)~System.Data.Common.DbTransaction", ""),
        ("M:Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction.Commit", ""),
        ("M:Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction.CommitAsync(System.Threading.CancellationToken)~System.Threading.Tasks.Task", ""),
        ("M:Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction.Rollback", ""),
        ("M:Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction.RollbackAsync(System.Threading.CancellationToken)~System.Threading.Tasks.Task", ""),
        ("M:Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction.get_TransactionId~System.Guid", ""),
        ("M:Microsoft.Extensions.Logging.EventId.#ctor(System.Int32,System.String)", ""),
        ("M:Microsoft.Extensions.Logging.ILogger.IsEnabled(Microsoft.Extensions.Logging.LogLevel)~System.Boolean", ""),
        ("M:Microsoft.Extensions.Logging.ILoggerFactory.CreateLogger(System.String)~Microsoft.Extensions.Logging.ILogger", ""),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.Log(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.LogLevel,Microsoft.Extensions.Logging.EventId,System.Exception,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.Log(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.LogLevel,Microsoft.Extensions.Logging.EventId,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.Log(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.LogLevel,System.Exception,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.Log(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.LogLevel,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogCritical(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.EventId,System.Exception,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogCritical(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.EventId,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogCritical(Microsoft.Extensions.Logging.ILogger,System.Exception,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogCritical(Microsoft.Extensions.Logging.ILogger,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.EventId,System.Exception,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.EventId,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(Microsoft.Extensions.Logging.ILogger,System.Exception,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogDebug(Microsoft.Extensions.Logging.ILogger,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogError(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.EventId,System.Exception,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogError(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.EventId,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogError(Microsoft.Extensions.Logging.ILogger,System.Exception,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogError(Microsoft.Extensions.Logging.ILogger,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.EventId,System.Exception,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.EventId,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(Microsoft.Extensions.Logging.ILogger,System.Exception,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(Microsoft.Extensions.Logging.ILogger,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogTrace(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.EventId,System.Exception,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogTrace(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.EventId,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogTrace(Microsoft.Extensions.Logging.ILogger,System.Exception,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogTrace(Microsoft.Extensions.Logging.ILogger,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.EventId,System.Exception,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Logging.EventId,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(Microsoft.Extensions.Logging.ILogger,System.Exception,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(Microsoft.Extensions.Logging.ILogger,System.String,System.Object[])", "DeepRead:args"),
        ("M:Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger(Microsoft.Extensions.Logging.ILoggerFactory,System.Type)~Microsoft.Extensions.Logging.ILogger", ""),
        ("M:Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger``1(Microsoft.Extensions.Logging.ILoggerFactory)~Microsoft.Extensions.Logging.ILogger{``0}", ""),
        ("M:Newtonsoft.Json.JsonConvert.DeserializeObject(System.String)~System.Object", ""),
        ("M:Newtonsoft.Json.JsonConvert.DeserializeObject(System.String,Newtonsoft.Json.JsonSerializerSettings)~System.Object", ""),
        ("M:Newtonsoft.Json.JsonConvert.DeserializeObject(System.String,System.Type)~System.Object", ""),
        ("M:Newtonsoft.Json.JsonConvert.DeserializeObject(System.String,System.Type,Newtonsoft.Json.JsonConverter[])~System.Object", ""),
        ("M:Newtonsoft.Json.JsonConvert.DeserializeObject(System.String,System.Type,Newtonsoft.Json.JsonSerializerSettings)~System.Object", ""),
        ("M:Newtonsoft.Json.JsonConvert.DeserializeObject``1(System.String)~``0", ""),
        ("M:Newtonsoft.Json.JsonConvert.DeserializeObject``1(System.String,Newtonsoft.Json.JsonConverter[])~``0", ""),
        ("M:Newtonsoft.Json.JsonConvert.DeserializeObject``1(System.String,Newtonsoft.Json.JsonSerializerSettings)~``0", ""),
        ("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object)~System.String", "DeepRead:value"),
        ("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.Formatting)~System.String", "DeepRead:value"),
        ("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.Formatting,Newtonsoft.Json.JsonConverter[])~System.String", "DeepRead:value"),
        ("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.Formatting,Newtonsoft.Json.JsonSerializerSettings)~System.String", "DeepRead:value"),
        ("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.JsonConverter[])~System.String", "DeepRead:value"),
        ("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.JsonSerializerSettings)~System.String", "DeepRead:value"),
        ("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,System.Type,Newtonsoft.Json.Formatting,Newtonsoft.Json.JsonSerializerSettings)~System.String", "DeepRead:value"),
        ("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,System.Type,Newtonsoft.Json.JsonSerializerSettings)~System.String", "DeepRead:value"),
        ("M:System.Array.IndexOf``1(``0[],``0)~System.Int32", "DeepRead:array,DeepRead:value"),
        ("M:System.Environment.get_ProcessorCount~System.Int32", ""),
        ("M:System.Environment.get_TickCount~System.Int32", ""),
        ("M:System.Linq.Enumerable.Any``1(System.Collections.Generic.IEnumerable{``0})~System.Boolean", "DeepRead:source"),
        ("M:System.Linq.Enumerable.AsEnumerable``1(System.Collections.Generic.IEnumerable{``0})~System.Collections.Generic.IEnumerable{``0}", "DeepRead:source"),
        ("M:System.Linq.Enumerable.Cast``1(System.Collections.IEnumerable)~System.Collections.Generic.IEnumerable{``0}", "DeepRead:source"),
        ("M:System.Linq.Enumerable.Contains``1(System.Collections.Generic.IEnumerable{``0},``0)~System.Boolean", "DeepRead:source,DeepRead:value"),
        ("M:System.Linq.Enumerable.Count``1(System.Collections.Generic.IEnumerable{``0})~System.Int32", "DeepRead:source"),
        ("M:System.Linq.Enumerable.Empty``1~System.Collections.Generic.IEnumerable{``0}", ""),
        ("M:System.Linq.Enumerable.FirstOrDefault``1(System.Collections.Generic.IEnumerable{``0})~``0", "DeepRead:source"),
        ("M:System.Linq.Enumerable.First``1(System.Collections.Generic.IEnumerable{``0})~``0", "DeepRead:source"),
        ("M:System.Linq.Enumerable.LastOrDefault``1(System.Collections.Generic.IEnumerable{``0})~``0", "DeepRead:source"),
        ("M:System.Linq.Enumerable.Last``1(System.Collections.Generic.IEnumerable{``0})~``0", "DeepRead:source"),
        ("M:System.Linq.Enumerable.LongCount``1(System.Collections.Generic.IEnumerable{``0})~System.Int64", "DeepRead:source"),
        ("M:System.Linq.Enumerable.OfType``1(System.Collections.IEnumerable)~System.Collections.Generic.IEnumerable{``0}", "DeepRead:source"),
        ("M:System.Linq.Enumerable.SequenceEqual``1(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``0})~System.Boolean", "DeepRead:first,DeepRead:second"),
        ("M:System.Linq.Enumerable.SingleOrDefault``1(System.Collections.Generic.IEnumerable{``0})~``0", "DeepRead:source"),
        ("M:System.Linq.Enumerable.Single``1(System.Collections.Generic.IEnumerable{``0})~``0", "DeepRead:source"),
        ("M:System.Linq.Enumerable.Skip``1(System.Collections.Generic.IEnumerable{``0},System.Int32)~System.Collections.Generic.IEnumerable{``0}", "DeepRead:source"),
        ("M:System.Linq.Enumerable.Take``1(System.Collections.Generic.IEnumerable{``0},System.Int32)~System.Collections.Generic.IEnumerable{``0}", "DeepRead:source"),
        ("M:System.Linq.Enumerable.Take``1(System.Collections.Generic.IEnumerable{``0},System.Range)~System.Collections.Generic.IEnumerable{``0}", "DeepRead:source"),
        ("M:System.Linq.Enumerable.ToArray``1(System.Collections.Generic.IEnumerable{``0})~``0[]", "DeepRead:source"),
        ("M:System.Linq.Enumerable.ToList``1(System.Collections.Generic.IEnumerable{``0})~System.Collections.Generic.List{``0}", "DeepRead:source"),
        ("M:System.Linq.Enumerable.Union``1(System.Collections.Generic.IEnumerable{``0},System.Collections.Generic.IEnumerable{``0})~System.Collections.Generic.IEnumerable{``0}", "DeepRead:first,DeepRead:second"),
        ("M:System.Linq.Queryable.Any``1(System.Linq.IQueryable{``0})~System.Boolean", ""),
        ("M:System.Linq.Queryable.Any``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}})~System.Boolean", ""),
        ("M:System.Linq.Queryable.OrderBy``2(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,``1}})~System.Linq.IOrderedQueryable{``0}", ""),
        ("M:System.Linq.Queryable.OrderBy``2(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,``1}},System.Collections.Generic.IComparer{``1})~System.Linq.IOrderedQueryable{``0}", ""),
        ("M:System.Linq.Queryable.SingleOrDefault``1(System.Linq.IQueryable{``0})~``0", ""),
        ("M:System.Linq.Queryable.SingleOrDefault``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}})~``0", ""),
        ("M:System.Linq.Queryable.SingleOrDefault``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}},``0)~``0", ""),
        ("M:System.Linq.Queryable.SingleOrDefault``1(System.Linq.IQueryable{``0},``0)~``0", ""),
        ("M:System.Linq.Queryable.Single``1(System.Linq.IQueryable{``0})~``0", ""),
        ("M:System.Linq.Queryable.Single``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}})~``0", ""),
        ("M:System.Linq.Queryable.Skip``1(System.Linq.IQueryable{``0},System.Int32)~System.Linq.IQueryable{``0}", ""),
        ("M:System.Linq.Queryable.Take``1(System.Linq.IQueryable{``0},System.Int32)~System.Linq.IQueryable{``0}", ""),
        ("M:System.Linq.Queryable.Take``1(System.Linq.IQueryable{``0},System.Range)~System.Linq.IQueryable{``0}", ""),
        ("M:System.Linq.Queryable.Where``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}})~System.Linq.IQueryable{``0}", ""),
        ("M:System.Linq.Queryable.Where``1(System.Linq.IQueryable{``0},System.Linq.Expressions.Expression{System.Func{``0,System.Int32,System.Boolean}})~System.Linq.IQueryable{``0}", ""),
        ("M:System.Net.Http.DelegatingHandler.#ctor", ""),
        ("M:System.Net.Http.DelegatingHandler.#ctor(System.Net.Http.HttpMessageHandler)", ""),
        ("M:System.Net.Http.Headers.AuthenticationHeaderValue.#ctor(System.String)", ""),
        ("M:System.Net.Http.Headers.AuthenticationHeaderValue.#ctor(System.String,System.String)", ""),
        ("M:System.Net.Http.Headers.HttpHeaders.Contains(System.String)~System.Boolean", ""),
        ("M:System.Net.Http.Headers.HttpHeaders.TryGetValues(System.String,System.Collections.Generic.IEnumerable{System.String}@)~System.Boolean", ""),
        ("M:System.Net.Http.HttpClient.GetAsync(System.String)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", ""),
        ("M:System.Net.Http.HttpClient.GetAsync(System.String,System.Net.Http.HttpCompletionOption)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", ""),
        ("M:System.Net.Http.HttpClient.GetAsync(System.String,System.Net.Http.HttpCompletionOption,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", ""),
        ("M:System.Net.Http.HttpClient.GetAsync(System.String,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", ""),
        ("M:System.Net.Http.HttpClient.GetAsync(System.Uri)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", ""),
        ("M:System.Net.Http.HttpClient.GetAsync(System.Uri,System.Net.Http.HttpCompletionOption)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", ""),
        ("M:System.Net.Http.HttpClient.GetAsync(System.Uri,System.Net.Http.HttpCompletionOption,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", ""),
        ("M:System.Net.Http.HttpClient.GetAsync(System.Uri,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", ""),
        ("M:System.Net.Http.HttpClient.GetStringAsync(System.String)~System.Threading.Tasks.Task{System.String}", ""),
        ("M:System.Net.Http.HttpClient.GetStringAsync(System.String,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.String}", ""),
        ("M:System.Net.Http.HttpClient.GetStringAsync(System.Uri)~System.Threading.Tasks.Task{System.String}", ""),
        ("M:System.Net.Http.HttpClient.GetStringAsync(System.Uri,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.String}", ""),
        ("M:System.Net.Http.HttpClient.PostAsync(System.String,System.Net.Http.HttpContent)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", "DeepRead:content"),
        ("M:System.Net.Http.HttpClient.PostAsync(System.String,System.Net.Http.HttpContent,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", "DeepRead:content"),
        ("M:System.Net.Http.HttpClient.PostAsync(System.Uri,System.Net.Http.HttpContent)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", "DeepRead:content"),
        ("M:System.Net.Http.HttpClient.PostAsync(System.Uri,System.Net.Http.HttpContent,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", "DeepRead:content"),
        ("M:System.Net.Http.HttpClient.PutAsync(System.String,System.Net.Http.HttpContent)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", "DeepRead:content"),
        ("M:System.Net.Http.HttpClient.PutAsync(System.String,System.Net.Http.HttpContent,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", "DeepRead:content"),
        ("M:System.Net.Http.HttpClient.PutAsync(System.Uri,System.Net.Http.HttpContent)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", "DeepRead:content"),
        ("M:System.Net.Http.HttpClient.PutAsync(System.Uri,System.Net.Http.HttpContent,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", "DeepRead:content"),
        ("M:System.Net.Http.HttpClient.SendAsync(System.Net.Http.HttpRequestMessage)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", "DeepRead:request"),
        ("M:System.Net.Http.HttpClient.SendAsync(System.Net.Http.HttpRequestMessage,System.Net.Http.HttpCompletionOption)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", "DeepRead:request"),
        ("M:System.Net.Http.HttpClient.SendAsync(System.Net.Http.HttpRequestMessage,System.Net.Http.HttpCompletionOption,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", "DeepRead:request"),
        ("M:System.Net.Http.HttpClient.SendAsync(System.Net.Http.HttpRequestMessage,System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}", "DeepRead:request"),
        ("M:System.Net.Http.HttpClient.get_DefaultRequestHeaders~System.Net.Http.Headers.HttpRequestHeaders", ""),
        ("M:System.Net.Http.HttpClientFactoryExtensions.CreateClient(System.Net.Http.IHttpClientFactory)~System.Net.Http.HttpClient", ""),
        ("M:System.Net.Http.HttpContent.ReadAsStringAsync(System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.String}", ""),
        ("M:System.Net.Http.HttpContent.ReadAsStringAsync~System.Threading.Tasks.Task{System.String}", ""),
        ("M:System.Net.Http.HttpMethod.get_Connect~System.Net.Http.HttpMethod", ""),
        ("M:System.Net.Http.HttpMethod.get_Delete~System.Net.Http.HttpMethod", ""),
        ("M:System.Net.Http.HttpMethod.get_Get~System.Net.Http.HttpMethod", ""),
        ("M:System.Net.Http.HttpMethod.get_Head~System.Net.Http.HttpMethod", ""),
        ("M:System.Net.Http.HttpMethod.get_Options~System.Net.Http.HttpMethod", ""),
        ("M:System.Net.Http.HttpMethod.get_Patch~System.Net.Http.HttpMethod", ""),
        ("M:System.Net.Http.HttpMethod.get_Post~System.Net.Http.HttpMethod", ""),
        ("M:System.Net.Http.HttpMethod.get_Put~System.Net.Http.HttpMethod", ""),
        ("M:System.Net.Http.HttpMethod.get_Query~System.Net.Http.HttpMethod", ""),
        ("M:System.Net.Http.HttpMethod.get_Trace~System.Net.Http.HttpMethod", ""),
        ("M:System.Net.Http.HttpRequestMessage.#ctor", ""),
        ("M:System.Net.Http.HttpRequestMessage.#ctor(System.Net.Http.HttpMethod,System.String)", ""),
        ("M:System.Net.Http.HttpRequestMessage.#ctor(System.Net.Http.HttpMethod,System.Uri)", ""),
        ("M:System.Net.Http.HttpRequestMessage.get_Headers~System.Net.Http.Headers.HttpRequestHeaders", ""),
        ("M:System.Net.Http.HttpRequestMessage.get_Method~System.Net.Http.HttpMethod", ""),
        ("M:System.Net.Http.HttpRequestMessage.get_RequestUri~System.Uri", ""),
        ("M:System.Net.Http.HttpResponseMessage.EnsureSuccessStatusCode~System.Net.Http.HttpResponseMessage", ""),
        ("M:System.Net.Http.HttpResponseMessage.get_Content~System.Net.Http.HttpContent", ""),
        ("M:System.Net.Http.HttpResponseMessage.get_Headers~System.Net.Http.Headers.HttpResponseHeaders", ""),
        ("M:System.Net.Http.HttpResponseMessage.get_IsSuccessStatusCode~System.Boolean", ""),
        ("M:System.Net.Http.HttpResponseMessage.get_ReasonPhrase~System.String", ""),
        ("M:System.Net.Http.HttpResponseMessage.get_RequestMessage~System.Net.Http.HttpRequestMessage", ""),
        ("M:System.Net.Http.HttpResponseMessage.get_StatusCode~System.Net.HttpStatusCode", ""),
        ("M:System.Net.Http.IHttpClientFactory.CreateClient(System.String)~System.Net.Http.HttpClient", ""),
        ("M:System.Net.Http.StringContent.#ctor(System.String)", ""),
        ("M:System.Net.Http.StringContent.#ctor(System.String,System.Net.Http.Headers.MediaTypeHeaderValue)", ""),
        ("M:System.Net.Http.StringContent.#ctor(System.String,System.Text.Encoding)", ""),
        ("M:System.Net.Http.StringContent.#ctor(System.String,System.Text.Encoding,System.Net.Http.Headers.MediaTypeHeaderValue)", ""),
        ("M:System.Net.Http.StringContent.#ctor(System.String,System.Text.Encoding,System.String)", ""),
        ("M:System.Object.#ctor", ""),
        ("M:System.Object.GetType~System.Type", ""),
        ("M:System.Object.ReferenceEquals(System.Object,System.Object)~System.Boolean", ""),
        ("M:System.String.Concat(System.ReadOnlySpan{System.String})~System.String", "DeepRead:values"),
        ("M:System.String.Concat(System.String[])~System.String", "DeepRead:values"),
        ("M:System.Text.Encoding.GetString(System.Byte[])~System.String", "DeepRead:bytes"),
        ("M:System.Text.Encoding.GetString(System.ReadOnlySpan{System.Byte})~System.String", "DeepRead:bytes"),
        ("M:System.Text.Json.JsonDocument.Parse(System.String,System.Text.Json.JsonDocumentOptions)~System.Text.Json.JsonDocument", ""),
        ("M:System.Text.Json.JsonDocument.get_RootElement~System.Text.Json.JsonElement", ""),
        ("M:System.Text.Json.JsonElement.EnumerateArray~System.Text.Json.JsonElement.ArrayEnumerator", ""),
        ("M:System.Text.Json.JsonElement.GetProperty(System.String)~System.Text.Json.JsonElement", ""),
        ("M:System.Text.Json.JsonElement.ToString~System.String", ""),
        ("M:System.Text.Json.JsonException.#ctor", ""),
        ("M:System.Text.Json.JsonException.#ctor(System.Runtime.Serialization.SerializationInfo,System.Runtime.Serialization.StreamingContext)", ""),
        ("M:System.Text.Json.JsonException.#ctor(System.String)", ""),
        ("M:System.Text.Json.JsonException.#ctor(System.String,System.Exception)", ""),
        ("M:System.Text.Json.JsonException.#ctor(System.String,System.String,System.Nullable{System.Int64},System.Nullable{System.Int64})", ""),
        ("M:System.Text.Json.JsonException.#ctor(System.String,System.String,System.Nullable{System.Int64},System.Nullable{System.Int64},System.Exception)", ""),
        ("M:System.Text.Json.JsonSerializer.Deserialize(System.String,System.Type,System.Text.Json.JsonSerializerOptions)~System.Object", ""),
        ("M:System.Text.Json.JsonSerializer.Deserialize``1(System.String,System.Text.Json.JsonSerializerOptions)~``0", ""),
        ("M:System.Text.Json.JsonSerializer.Serialize(System.Object,System.Type,System.Text.Json.JsonSerializerOptions)~System.String", "DeepRead:value"),
        ("M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(System.Object,System.Type,System.Text.Json.JsonSerializerOptions)~System.Byte[]", "DeepRead:value"),
        ("M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes``1(``0,System.Text.Json.JsonSerializerOptions)~System.Byte[]", "DeepRead:value"),
        ("M:System.Text.Json.JsonSerializer.Serialize``1(``0,System.Text.Json.JsonSerializerOptions)~System.String", "DeepRead:value"),
        ("M:System.Text.Json.JsonSerializerOptions.#ctor", ""),
        ("M:System.Text.Json.JsonSerializerOptions.#ctor(System.Text.Json.JsonSerializerDefaults)", ""),
        ("M:System.Text.Json.Utf8JsonReader.GetInt32~System.Int32", ""),
        ("M:System.Text.Json.Utf8JsonReader.GetString~System.String", ""),
        ("M:System.Text.Json.Utf8JsonReader.get_TokenType~System.Text.Json.JsonTokenType", ""),
        ("T:System.Boolean", ""),
        ("T:System.Byte", ""),
        ("T:System.Char", ""),
        ("T:System.Collections.Generic.KeyValuePair`2", ""),
        ("T:System.Convert", ""),
        ("T:System.DateOnly", ""),
        ("T:System.DateTime", ""),
        ("T:System.DateTimeOffset", ""),
        ("T:System.Decimal", ""),
        ("T:System.Double", ""),
        ("T:System.Enum", ""),
        ("T:System.Exception", "IncludesDerived"),
        ("T:System.Guid", ""),
        ("T:System.Int16", ""),
        ("T:System.Int32", ""),
        ("T:System.Int64", ""),
        ("T:System.Math", ""),
        ("T:System.Nullable`1", ""),
        ("T:System.SByte", ""),
        ("T:System.Single", ""),
        ("T:System.String", ""),
        ("T:System.StringComparer", ""),
        ("T:System.Text.Encoding", ""),
        ("T:System.TimeOnly", ""),
        ("T:System.TimeSpan", ""),
        ("T:System.Type", ""),
        ("T:System.UInt16", ""),
        ("T:System.UInt32", ""),
        ("T:System.UInt64", ""),
        ("T:System.Uri", ""),
        ("T:System.Version", "")
    ];

    // ---- every entry against the real metadata ----

    [Fact]
    public void Every_member_resolves_to_exactly_one_member_of_its_assembly_in_its_range()
    {
        Assert.All(Table.Members, member =>
        {
            var method = Assert.IsAssignableFrom<IMethodSymbol>(Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(member.Id, Real.Value)));
            Assert.Contains(member.Assemblies, range => range.AssemblyName == method.ContainingAssembly.Identity.Name &&
                                                        range.Contains(method.ContainingAssembly.Identity.Version));
        });
    }

    [Fact]
    public void Every_immutable_type_resolves_to_exactly_one_type_of_its_assembly_in_its_range()
    {
        Assert.All(Table.ImmutableTypes, type =>
        {
            var symbol = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(type.Id, Real.Value)));
            Assert.Contains(type.Assemblies, range => range.AssemblyName == symbol.ContainingAssembly.Identity.Name &&
                                                      range.Contains(symbol.ContainingAssembly.Identity.Version));
        });
    }

    [Fact]
    public void Every_effect_names_an_existing_parameter()
    {
        Assert.All(Table.Members.Where(member => member.Effects.Count != 0), member =>
        {
            var method = (IMethodSymbol)DocumentationCommentId.GetSymbolsForDeclarationId(member.Id, Real.Value).Single();
            Assert.All(member.Effects, effect => Assert.Contains(method.Parameters, parameter => parameter.Name == effect.Parameter));
        });
    }

    [Fact]
    public void No_member_takes_a_delegate_or_is_a_setter()
    {
        Assert.All(Table.Members, member =>
        {
            var method = (IMethodSymbol)DocumentationCommentId.GetSymbolsForDeclarationId(member.Id, Real.Value).Single();
            Assert.NotEqual(MethodKind.PropertySet, method.MethodKind);
            Assert.DoesNotContain(method.Parameters, parameter => parameter.Type.TypeKind == TypeKind.Delegate);
        });
    }

    [Fact]
    public void Table_is_exactly_the_phase_5a_list()
    {
        var table = Table.Members.Select(member => (member.Id, Effects(member.Effects)))
                         .Concat(Table.ImmutableTypes.Select(type => (type.Id, Effects: type.IncludesDerived ? "IncludesDerived" : "")))
                         .ToHashSet();
        var list = Phase5aList.ToHashSet();

        Assert.Equal(Phase5aList.Length, list.Count);
        Assert.Empty(table.Except(list));
        Assert.Empty(list.Except(table));
    }

    [Fact]
    public void Every_overload_of_an_all_overloads_member_is_listed_or_excluded()
    {
        var listed = Table.Members.Select(member => member.Id).ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();
        var checkedOverloads = 0;
        foreach (var (typeName, names, excluded) in AllOverloads)
        {
            var type = Real.Value.GetTypeByMetadataName(typeName) ?? throw new InvalidOperationException($"{typeName} is not in the metadata.");
            foreach (var overload in names.SelectMany(name => type.GetMembers(name)).OfType<IMethodSymbol>()
                                          .Where(method => method.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected))
            {
                checkedOverloads++;
                var id = DocumentationCommentId.CreateDeclarationId(overload)!;
                var rule = Rules(excluded).FirstOrDefault(candidate => candidate.Rejects(overload)).Name;
                if (listed.Contains(id) == (rule is not null))
                    violations.Add($"{id}: {(listed.Contains(id) ? $"listed although {rule} rejects it" : "neither listed nor rejected by a rule")}");
            }
        }

        Assert.True(checkedOverloads > 150, $"only {checkedOverloads} overloads were checked");
        Assert.Empty(violations);
    }

    // ---- the immutable-type rule ----

    [Fact]
    public void Immutable_rule_knows_members_whose_arguments_are_all_immutable()
    {
        var calls = Calls("""
            using System;
            using System.Collections.Generic;
            class C
            {
                void M(string s, Exception e)
                {
                    string.IsNullOrEmpty(s);
                    int.TryParse(s, out var number);
                    new ArgumentNullException(nameof(s));
                    ((int?)number).GetValueOrDefault(2);
                    new KeyValuePair<string, int>(s, number);
                    Math.Max(1, number);
                    _ = DateTime.Now;
                    _ = e.Message;
                    s.Split(',', StringSplitOptions.RemoveEmptyEntries);
                }
            }
            """);

        Assert.Equal(9, calls.Count);
        Assert.All(calls, call => Assert.Equal((LibraryMatchKind.Known, 0), (Table.Find(call)!.Kind, Table.Find(call)!.Effects.Count)));
    }

    [Fact]
    public void Immutable_rule_does_not_know_a_setter()
    {
        var setters = ImmutableTypeMembers().Where(method => method.MethodKind == MethodKind.PropertySet).ToArray();

        Assert.Contains(setters, setter => setter.Name == "set_Source" && setter.ContainingType.Name == "Exception");
        Assert.All(setters, setter => Assert.Null(Table.Find(setter)));
    }

    [Fact]
    public void Immutable_rule_does_not_know_a_member_taking_a_delegate()
    {
        var members = ImmutableTypeMembers().Where(method => method.Parameters.Any(parameter => parameter.Type.TypeKind == TypeKind.Delegate)).ToArray();

        Assert.Contains(members, method => method.Name == "Create" && method.ContainingType.SpecialType == SpecialType.System_String);
        Assert.All(members, method => Assert.Null(Table.Find(method)));
    }

    [Fact]
    public void Immutable_rule_does_not_know_a_member_taking_a_mutable_argument()
    {
        var calls = Calls("""
            using System.Collections.Generic;
            class C
            {
                void M(string s, List<string> parts, object value)
                {
                    string.Join(",", parts);
                    string.Format("{0}", value);
                    s.CopyTo(0, new char[1], 0, 1);
                }
            }
            """);

        Assert.Equal(3, calls.Count);
        Assert.All(calls, call => Assert.Null(Table.Find(call)));
    }

    [Fact]
    public void Immutable_rule_does_not_know_a_ref_or_in_parameter()
    {
        // No public member of a listed type takes an immutable argument by reference, so the rule is exercised on a type of that
        // name declared in an assembly of the family's identity: only the passing mode differs between the three methods.
        var compilation = CSharpCompilation.Create(
            "System.Runtime",
            [CSharpSyntaxTree.ParseText("""
                [assembly: System.Reflection.AssemblyVersion("10.0.0.0")]
                namespace System
                {
                    public static class Math
                    {
                        public static int ByValue(int value) => value;
                        public static int ByRef(ref int value) => value;
                        public static int ByIn(in int value) => value;
                    }
                }
                """)],
            StubAssemblies.PlatformWithout(["System.Runtime"]),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var math = compilation.Assembly.GetTypeByMetadataName("System.Math")!;

        Assert.Equal(LibraryMatchKind.Known, Table.Find(math.GetMembers("ByValue").OfType<IMethodSymbol>().Single())?.Kind);
        Assert.Null(Table.Find(math.GetMembers("ByRef").OfType<IMethodSymbol>().Single()));
        Assert.Null(Table.Find(math.GetMembers("ByIn").OfType<IMethodSymbol>().Single()));
    }

    [Fact]
    public void Immutable_rule_knows_an_exception_of_another_framework_assembly()
    {
        var call = Assert.Single(Calls("""
            class C { void M(string s) { new System.Net.Http.HttpRequestException(s); } }
            """));

        var match = Table.Find(call);

        Assert.Equal((LibraryMatchKind.Known, "System.Net.Http"), (match?.Kind, match?.Assembly.Name));
    }

    [Fact]
    public void Immutable_rule_names_every_member_out_of_range_whatever_its_parameter_types()
    {
        // Out of range, a parameter of the type's own is no reason to leave the member undescribed: it is the same member, and it
        // is counted as the other out-of-range calls are (R5).
        var compilation = CSharpCompilation.Create(
            "System.Private.Uri",
            [CSharpSyntaxTree.ParseText("""
                [assembly: System.Reflection.AssemblyVersion("12.0.0.0")]
                namespace System
                {
                    public class Uri
                    {
                        public Uri(string uriString) { }
                        public Uri(Uri baseUri, string relativeUri) { }
                    }
                }
                """)],
            StubAssemblies.PlatformWithout(["System.Private.Uri"]),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var constructors = compilation.Assembly.GetTypeByMetadataName("System.Uri")!.InstanceConstructors;

        Assert.Equal(2, constructors.Length);
        Assert.All(constructors, constructor => Assert.Equal(LibraryMatchKind.OutOfRange, Table.Find(constructor)?.Kind));
    }

    [Fact]
    public void Out_of_range_framework_assembly_declaring_an_exception_is_an_out_of_range_reference()
    {
        static MetadataReference Framework(string name, string source) =>
            CSharpCompilation.Create(name,
                                     [CSharpSyntaxTree.ParseText($$"""[assembly: System.Reflection.AssemblyVersion("12.0.0.0")] {{source}}""")],
                                     StubAssemblies.PlatformWithout([name]),
                                     new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).ToMetadataReference();
        var converter = Framework("System.ComponentModel.TypeConverter",
                                  "namespace System.ComponentModel { public class WarningException : System.SystemException { public WarningException(string message) { } } }");
        var memory = Framework("System.Memory", "namespace System.Buffers { public sealed class Pool { } }");
        var calls = Calls("""
            class C { void M(string s) { new System.ComponentModel.WarningException(s); new System.Buffers.Pool(); } }
            """,
            StubAssemblies.PlatformWithout(["System.ComponentModel.TypeConverter", "System.Memory"]).Append(converter).Append(memory));
        var compilation = CSharpCompilation.Create("LibraryTable", [], StubAssemblies.PlatformWithout(["System.ComponentModel.TypeConverter", "System.Memory"])
                                                                           .Append(converter).Append(memory));

        Assert.Equal(LibraryMatchKind.OutOfRange, Table.Find(calls[0])?.Kind);
        var reference = Assert.Single(Table.OutOfRangeReferences(compilation));
        Assert.Equal(("System.ComponentModel.TypeConverter", new Version(12, 0, 0, 0)), (reference.Assembly.Name, reference.Assembly.Version));
        Assert.Equal((new Version(8, 0, 0, 0), new Version(11, 0, 0, 0)), (reference.Range.Minimum, reference.Range.MaximumExclusive));
    }

    // ---- the self-check ----

    [Fact]
    public void Repeated_member_fails_the_build()
    {
        var exception = Assert.Throws<LibrarySemanticsException>(() => new LibrarySemanticsTable([Member(), Member()], []));

        Assert.Contains("twice", exception.Message);
    }

    [Fact]
    public void Empty_assembly_name_fails_the_build()
    {
        Assert.Throws<LibrarySemanticsException>(() => new LibrarySemanticsTable([Member() with { Assemblies = [SupportedAssemblyVersion.Framework(" ")] }], []));
        Assert.Throws<LibrarySemanticsException>(() => new LibrarySemanticsTable([], [new ImmutableLibraryType("T:Library.Value", [SupportedAssemblyVersion.Framework("")])]));
    }

    [Fact]
    public void Range_whose_minimum_is_not_below_its_maximum_fails_the_build()
    {
        var empty = new SupportedAssemblyVersion("Library", new Version(13, 0, 0, 0), new Version(13, 0, 0, 0));
        var inverted = new SupportedAssemblyVersion("Library", new Version(14, 0, 0, 0), new Version(13, 0, 0, 0));

        Assert.Throws<LibrarySemanticsException>(() => new LibrarySemanticsTable([Member() with { Assemblies = [empty] }], []));
        Assert.Throws<LibrarySemanticsException>(() => new LibrarySemanticsTable([Member() with { Assemblies = [inverted] }], []));
    }

    [Fact]
    public void Effect_without_a_parameter_name_fails_the_build()
    {
        Assert.Throws<LibrarySemanticsException>(() => new LibrarySemanticsTable([Member() with { Effects = [LibraryEffect.DeepReadOf("")] }], []));
    }

    [Fact]
    public void Repeated_effect_fails_the_build()
    {
        var repeated = Member() with { Effects = [LibraryEffect.DeepReadOf("value"), LibraryEffect.DeepReadOf("value")] };
        var distinct = Member() with { Effects = [LibraryEffect.DeepReadOf("value"), LibraryEffect.WriteOf("value")] };

        Assert.Throws<LibrarySemanticsException>(() => new LibrarySemanticsTable([repeated], []));
        Assert.Single(new LibrarySemanticsTable([distinct], []).Members);
    }

    [Fact]
    public void Type_a_phase_3_or_4_recognizer_owns_fails_the_build()
    {
        Assert.Throws<LibrarySemanticsException>(() => new LibrarySemanticsTable([Member() with { Id = "M:System.Threading.Tasks.Task.Run(System.Action)~System.Threading.Tasks.Task" }], []));
        Assert.Throws<LibrarySemanticsException>(() => new LibrarySemanticsTable([Member() with { Id = "M:System.Threading.Interlocked.Increment(System.Int32@)~System.Int32" }], []));
        Assert.Throws<LibrarySemanticsException>(() => new LibrarySemanticsTable([], [new ImmutableLibraryType("T:System.Collections.Generic.List`1", [SupportedAssemblyVersion.Framework("System.Runtime")])]));
    }

    // ---- identity at a call ----

    [Fact]
    public void Another_overload_of_a_listed_name_is_not_known()
    {
        var calls = Calls("""
            using System.IO;
            using System.Text.Json;
            class C
            {
                void M(Stream stream, object value)
                {
                    JsonSerializer.Serialize(value);
                    JsonSerializer.Serialize(stream, value);
                }
            }
            """);

        Assert.Equal(LibraryMatchKind.Known, Table.Find(calls[0])?.Kind);
        Assert.Null(Table.Find(calls[1]));
    }

    [Fact]
    public void Member_in_range_is_known_on_the_real_package()
    {
        var calls = Calls("""
            using System.Text.Json;
            using Microsoft.Extensions.Logging;
            using Newtonsoft.Json;
            class Order { public int Id; }
            class C
            {
                void M(Order order, ILogger logger)
                {
                    System.Text.Json.JsonSerializer.Serialize(order);
                    JsonConvert.SerializeObject(order);
                    logger.LogInformation("order {Order}", order);
                }
            }
            """);

        Assert.Equal(3, calls.Count);
        Assert.All(calls, call => Assert.Equal(LibraryMatchKind.Known, Table.Find(call)?.Kind));
        Assert.Equal(("System.Text.Json", "value"), Summary(Table.Find(calls[0])!));
        Assert.Equal(("Newtonsoft.Json", "value"), Summary(Table.Find(calls[1])!));
        Assert.Equal(("Microsoft.Extensions.Logging.Abstractions", "args"), Summary(Table.Find(calls[2])!));
        Assert.Equal(new Version(13, 0, 0, 0), Table.Find(calls[1])!.Assembly.Version);
    }

    [Fact]
    public void Same_member_out_of_range_is_not_known_and_is_named_out_of_range()
    {
        var calls = Calls("""
            using Newtonsoft.Json;
            class C { void M(object value) { JsonConvert.SerializeObject(value); } }
            """,
            StubAssemblies.PlatformWithout([]).Append(StubAssemblies.Get(StubAssemblies.NEWTONSOFT_JSON, 12)));

        var match = Table.Find(Assert.Single(calls));

        Assert.NotNull(match);
        Assert.Equal(LibraryMatchKind.OutOfRange, match.Kind);
        Assert.Equal(("Newtonsoft.Json", new Version(12, 0, 0, 0)), (match.Assembly.Name, match.Assembly.Version));
        Assert.Equal((new Version(13, 0, 0, 0), new Version(14, 0, 0, 0)), (match.Range.Minimum, match.Range.MaximumExclusive));
    }

    [Fact]
    public void Implementation_and_reference_assembly_names_of_a_framework_type_are_both_accepted()
    {
        const string Source = """
            class C { void M(string s, object a) { string.IsNullOrEmpty(s); object.ReferenceEquals(a, s); new System.Uri(s); } }
            """;
        var referencePack = Path.GetFullPath(Path.Combine(RuntimeDirectory, "..", "..", "..", "packs", "Microsoft.NETCore.App.Ref",
                                                          Path.GetFileName(RuntimeDirectory), "ref", $"net{Environment.Version.Major}.0"));
        Assert.True(Directory.Exists(referencePack), $"No reference pack at {referencePack}.");

        var implementation = Calls(Source, StubAssemblies.PlatformWithout([])).Select(call => Table.Find(call)).ToArray();
        var reference = Calls(Source, Directory.GetFiles(referencePack, "*.dll").Select(path => MetadataReference.CreateFromFile(path)))
            .Select(call => Table.Find(call)).ToArray();

        Assert.Equal(["System.Private.CoreLib", "System.Private.CoreLib", "System.Private.Uri"], implementation.Select(match => match!.Assembly.Name));
        Assert.Equal(["System.Runtime", "System.Runtime", "System.Runtime"], reference.Select(match => match!.Assembly.Name));
        Assert.All(implementation.Concat(reference), match => Assert.Equal(LibraryMatchKind.Known, match!.Kind));
    }

    [Fact]
    public void Same_named_member_of_another_assembly_is_not_known()
    {
        var calls = Calls("""
            namespace Newtonsoft.Json { public static class JsonConvert { public static string SerializeObject(object? value) => ""; } }
            class C { void M(object value) { Newtonsoft.Json.JsonConvert.SerializeObject(value); } }
            """,
            StubAssemblies.PlatformWithout([]));

        Assert.Null(Table.Find(Assert.Single(calls)));
    }

    // ---- helpers ----

    /// <summary>The members "Состав таблицы" lists with every overload, with the parameter types their family excludes.</summary>
    private static readonly (string Type, string[] Names, string[] Excluded)[] AllOverloads =
    [
        ("System.Text.Json.JsonSerializer", ["Serialize", "SerializeToUtf8Bytes", "Deserialize"],
         ["System.IO.Stream", "System.Text.Json.Utf8JsonWriter", "System.Text.Json.Utf8JsonReader", "System.IO.Pipelines.PipeWriter",
          "System.IO.Pipelines.PipeReader", "System.Text.Json.Serialization.Metadata.JsonTypeInfo",
          "System.Text.Json.Serialization.Metadata.JsonTypeInfo<TValue>", "System.Text.Json.Serialization.JsonSerializerContext",
          "System.Text.Json.Nodes.JsonNode", "System.Text.Json.JsonElement", "System.Text.Json.JsonDocument", "System.ReadOnlySpan<char>",
          "System.ReadOnlySpan<byte>", "System.Threading.CancellationToken"]),
        ("Newtonsoft.Json.JsonConvert", ["SerializeObject", "DeserializeObject"], []),
        ("Microsoft.Extensions.Logging.LoggerExtensions", ["LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical", "Log"], []),
        ("System.Net.Http.HttpClient", ["GetAsync", "GetStringAsync", "PostAsync", "PutAsync", "SendAsync"], []),
        ("System.Net.Http.HttpContent", ["ReadAsStringAsync"], []),
        ("Microsoft.EntityFrameworkCore.DbContext",
         ["Add", "AddAsync", "Update", "Attach", "Remove", "Entry", "AddRange", "AddRangeAsync", "UpdateRange", "AttachRange", "RemoveRange",
          "SaveChanges", "SaveChangesAsync"], []),
        ("Microsoft.EntityFrameworkCore.DbSet`1",
         ["Add", "AddAsync", "Update", "Attach", "Remove", "Entry", "AddRange", "AddRangeAsync", "UpdateRange", "AttachRange", "RemoveRange"], []),
        ("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions",
         ["ToListAsync", "ToArrayAsync", "SingleOrDefaultAsync", "SingleAsync", "FirstOrDefaultAsync", "FirstAsync", "AnyAsync", "CountAsync",
          "LongCountAsync", "Include"], []),
        ("System.Linq.Enumerable",
         ["Any", "Count", "LongCount", "First", "Single", "Last", "ToList", "ToArray", "Skip", "Take", "OfType", "Cast", "AsEnumerable", "Contains",
          "SequenceEqual", "Union", "Empty"], []),
        ("System.Linq.Queryable", ["Where", "Any", "Skip", "Take", "OrderBy", "SingleOrDefault", "Single"], [])
    ];

    /// <summary>The named rules an overload may be rejected by, in the order they are asked: the first that applies names it.</summary>
    private static (string Name, Func<IMethodSymbol, bool> Rejects)[] Rules(string[] excluded) =>
    [
        ("a delegate parameter", method => method.Parameters.Any(parameter => parameter.Type.TypeKind == TypeKind.Delegate)),
        ("an IEqualityComparer parameter", method => method.Parameters.Any(parameter => parameter.Type.OriginalDefinition.Name == "IEqualityComparer")),
        ("a parameter type the family excludes",
         method => method.Parameters.Any(parameter => excluded.Contains(parameter.Type.ToDisplayString().TrimEnd('?'), StringComparer.Ordinal))),
        ("a Deserialize input other than String",
         method => method.Name == "Deserialize" && method.Parameters[0].Type.SpecialType != SpecialType.System_String),
        ("a setter", method => method.MethodKind == MethodKind.PropertySet)
    ];

    private static string Effects(IEnumerable<LibraryEffect> effects) =>
        string.Join(",", effects.OrderBy(effect => effect.Kind).ThenBy(effect => effect.Parameter, StringComparer.Ordinal)
                                .Select(effect => $"{effect.Kind}:{effect.Parameter}"));

    private static (string Assembly, string Parameter) Summary(LibraryMatch match) =>
        (match.Assembly.Name, Assert.Single(match.Effects, effect => effect.Kind == LibraryEffectKind.DeepRead).Parameter);

    private static LibraryMember Member() =>
        new("M:Library.Api.Read(System.Object)", [new SupportedAssemblyVersion("Library", new Version(1, 0, 0, 0), new Version(2, 0, 0, 0))], []);

    /// <summary>Every public or protected method of the listed immutable types, derived types of <c>Exception</c> left out.</summary>
    private static IEnumerable<IMethodSymbol> ImmutableTypeMembers() =>
        Table.ImmutableTypes.SelectMany(type => DocumentationCommentId.GetSymbolsForDeclarationId(type.Id, Real.Value).OfType<INamedTypeSymbol>())
             .SelectMany(type => type.GetMembers().OfType<IMethodSymbol>())
             .Where(method => method.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected);

    /// <summary>The methods <paramref name="source"/> calls, in order: invocations, object creations and property reads.</summary>
    private static List<IMethodSymbol> Calls(string source, IEnumerable<MetadataReference>? references = null)
    {
        var compilation = references is null ? Real.Value.AddSyntaxTrees(Parse(source)) : Compile(source, references);
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));

        var tree = compilation.SyntaxTrees.Last();
        var model = compilation.GetSemanticModel(tree);
        return tree.GetRoot().DescendantNodes()
                   .Where(node => node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax ||
                                  node is MemberAccessExpressionSyntax && node.Parent is not InvocationExpressionSyntax)
                   .Select(node => model.GetSymbolInfo(node).Symbol switch
                   {
                       IMethodSymbol method => method,
                       IPropertySymbol property => property.GetMethod,
                       _ => null
                   })
                   .OfType<IMethodSymbol>()
                   .ToList();
    }

    private static CSharpCompilation Compile(string source, IEnumerable<MetadataReference> references) =>
        CSharpCompilation.Create("LibraryTable", source.Length == 0 ? [] : [Parse(source)], references,
                                 new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static SyntaxTree Parse(string source) =>
        CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
}
