using static ConcurrencyHunter.Providers.LibrarySemantics.LibraryEffect;

namespace ConcurrencyHunter.Providers.LibrarySemantics;

/// <summary>EF Core as opaque persistence (R2): a member that hands an entity to a <c>DbContext</c> or a <c>DbSet</c> reads it deep
/// and writes its own fields, since tracking may set its keys and navigations; saving, querying, transactions and the other
/// members the census met touch nothing, and the context's own state is no resource until phase 5e. Model configuration,
/// migrations, registration, <c>ExecuteAsync</c> with its delegate and the loaders that write a navigation nobody can name are
/// not known.</summary>
internal static class EntityFrameworkFamily
{
    private const string CONTEXT = "Microsoft.EntityFrameworkCore.DbContext";
    private const string SET = "Microsoft.EntityFrameworkCore.DbSet`1";
    private const string ENTRY = "Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry";
    private const string QUERYABLE = "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions";
    private const string FACADE = "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade";
    private const string RELATIONAL_FACADE = "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions";
    private const string TRANSACTION = "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction";
    private const string SOURCE = "System.Linq.IQueryable{``0}";
    private const string PREDICATE = "System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}}";
    private const string TOKEN = "System.Threading.CancellationToken";

    private static readonly SupportedAssemblyVersion[] Assemblies = [SupportedAssemblyVersion.Framework("Microsoft.EntityFrameworkCore")];
    private static readonly SupportedAssemblyVersion[] RelationalAssemblies =
        [SupportedAssemblyVersion.Framework("Microsoft.EntityFrameworkCore.Relational")];

    internal static IEnumerable<LibraryMember> Members =>
    [
        Entity($"M:{CONTEXT}.Add``1(``0)~{ENTRY}{{``0}}", "entity"),
        Entity($"M:{CONTEXT}.Add(System.Object)~{ENTRY}", "entity"),
        Entity($"M:{CONTEXT}.AddAsync``1(``0,{TOKEN})~System.Threading.Tasks.ValueTask{{{ENTRY}{{``0}}}}", "entity"),
        Entity($"M:{CONTEXT}.AddAsync(System.Object,{TOKEN})~System.Threading.Tasks.ValueTask{{{ENTRY}}}", "entity"),
        Entity($"M:{CONTEXT}.Update``1(``0)~{ENTRY}{{``0}}", "entity"),
        Entity($"M:{CONTEXT}.Update(System.Object)~{ENTRY}", "entity"),
        Entity($"M:{CONTEXT}.Attach``1(``0)~{ENTRY}{{``0}}", "entity"),
        Entity($"M:{CONTEXT}.Attach(System.Object)~{ENTRY}", "entity"),
        Entity($"M:{CONTEXT}.Remove``1(``0)~{ENTRY}{{``0}}", "entity"),
        Entity($"M:{CONTEXT}.Remove(System.Object)~{ENTRY}", "entity"),
        Entity($"M:{CONTEXT}.Entry``1(``0)~{ENTRY}{{``0}}", "entity"),
        Entity($"M:{CONTEXT}.Entry(System.Object)~{ENTRY}", "entity"),
        .. new[] { "AddRange", "UpdateRange", "AttachRange", "RemoveRange" }.SelectMany(name => new[]
        {
            Entity($"M:{CONTEXT}.{name}(System.Object[])", "entities"),
            Entity($"M:{CONTEXT}.{name}(System.Collections.Generic.IEnumerable{{System.Object}})", "entities"),
            Entity($"M:{SET}.{name}(`0[])", "entities"),
            Entity($"M:{SET}.{name}(System.Collections.Generic.IEnumerable{{`0}})", "entities")
        }),
        Entity($"M:{CONTEXT}.AddRangeAsync(System.Object[])~System.Threading.Tasks.Task", "entities"),
        Entity($"M:{CONTEXT}.AddRangeAsync(System.Collections.Generic.IEnumerable{{System.Object}},{TOKEN})~System.Threading.Tasks.Task", "entities"),
        Entity($"M:{SET}.AddRangeAsync(`0[])~System.Threading.Tasks.Task", "entities"),
        Entity($"M:{SET}.AddRangeAsync(System.Collections.Generic.IEnumerable{{`0}},{TOKEN})~System.Threading.Tasks.Task", "entities"),
        Entity($"M:{SET}.Add(`0)~{ENTRY}{{`0}}", "entity"),
        Entity($"M:{SET}.AddAsync(`0,{TOKEN})~System.Threading.Tasks.ValueTask{{{ENTRY}{{`0}}}}", "entity"),
        Entity($"M:{SET}.Update(`0)~{ENTRY}{{`0}}", "entity"),
        Entity($"M:{SET}.Attach(`0)~{ENTRY}{{`0}}", "entity"),
        Entity($"M:{SET}.Remove(`0)~{ENTRY}{{`0}}", "entity"),
        Entity($"M:{SET}.Entry(`0)~{ENTRY}{{`0}}", "entity"),
        Known($"M:{CONTEXT}.#ctor"),
        Known($"M:{CONTEXT}.#ctor(Microsoft.EntityFrameworkCore.DbContextOptions)"),
        Known($"M:{CONTEXT}.SaveChanges~System.Int32"),
        Known($"M:{CONTEXT}.SaveChanges(System.Boolean)~System.Int32"),
        Known($"M:{CONTEXT}.SaveChangesAsync({TOKEN})~System.Threading.Tasks.Task{{System.Int32}}"),
        Known($"M:{CONTEXT}.SaveChangesAsync(System.Boolean,{TOKEN})~System.Threading.Tasks.Task{{System.Int32}}"),
        Known($"M:{CONTEXT}.Find(System.Type,System.Object[])~System.Object"),
        Known($"M:{CONTEXT}.Find``1(System.Object[])~``0"),
        Known($"M:{CONTEXT}.FindAsync(System.Type,System.Object[])~System.Threading.Tasks.ValueTask{{System.Object}}"),
        Known($"M:{CONTEXT}.FindAsync(System.Type,System.Object[],{TOKEN})~System.Threading.Tasks.ValueTask{{System.Object}}"),
        Known($"M:{CONTEXT}.FindAsync``1(System.Object[])~System.Threading.Tasks.ValueTask{{``0}}"),
        Known($"M:{CONTEXT}.FindAsync``1(System.Object[],{TOKEN})~System.Threading.Tasks.ValueTask{{``0}}"),
        Known($"M:{SET}.Find(System.Object[])~`0"),
        Known($"M:{SET}.FindAsync(System.Object[])~System.Threading.Tasks.ValueTask{{`0}}"),
        Known($"M:{SET}.FindAsync(System.Object[],{TOKEN})~System.Threading.Tasks.ValueTask{{`0}}"),
        Known($"M:{CONTEXT}.get_Database~{FACADE}"),
        Known($"M:{CONTEXT}.get_ChangeTracker~Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker"),
        Known($"M:{SET}.get_Local~Microsoft.EntityFrameworkCore.ChangeTracking.LocalView{{`0}}"),
        Known($"M:{ENTRY}`1.get_Entity~`0"),
        Known($"M:{CONTEXT}.Dispose"),
        Known($"M:{CONTEXT}.DisposeAsync~System.Threading.Tasks.ValueTask"),
        Known("M:Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker.Entries``1~System.Collections.Generic.IEnumerable{" + ENTRY + "{``0}}"),
        Known($"M:{ENTRY}`1.Collection``1(System.Linq.Expressions.Expression{{System.Func{{`0,System.Collections.Generic.IEnumerable{{``0}}}}}})" +
              "~Microsoft.EntityFrameworkCore.ChangeTracking.CollectionEntry{`0,``0}"),
        Known($"M:{ENTRY}`1.Reference``1(System.Linq.Expressions.Expression{{System.Func{{`0,``0}}}})" +
              "~Microsoft.EntityFrameworkCore.ChangeTracking.ReferenceEntry{`0,``0}"),
        Known($"M:{QUERYABLE}.ToListAsync``1({SOURCE},{TOKEN})~System.Threading.Tasks.Task{{System.Collections.Generic.List{{``0}}}}"),
        Known($"M:{QUERYABLE}.ToArrayAsync``1({SOURCE},{TOKEN})~System.Threading.Tasks.Task{{``0[]}}"),
        .. new[]
        {
            ("SingleOrDefaultAsync", "``0"), ("SingleAsync", "``0"), ("FirstOrDefaultAsync", "``0"), ("FirstAsync", "``0"),
            ("AnyAsync", "System.Boolean"), ("CountAsync", "System.Int32"), ("LongCountAsync", "System.Int64")
        }.SelectMany(query => new[]
        {
            Known($"M:{QUERYABLE}.{query.Item1}``1({SOURCE},{TOKEN})~System.Threading.Tasks.Task{{{query.Item2}}}"),
            Known($"M:{QUERYABLE}.{query.Item1}``1({SOURCE},{PREDICATE},{TOKEN})~System.Threading.Tasks.Task{{{query.Item2}}}")
        }),
        Known($"M:{QUERYABLE}.Include``2({SOURCE},System.Linq.Expressions.Expression{{System.Func{{``0,``1}}}})" +
              "~Microsoft.EntityFrameworkCore.Query.IIncludableQueryable{``0,``1}"),
        Known($"M:{QUERYABLE}.Include``1({SOURCE},System.String)~{SOURCE}"),
        Known($"M:{FACADE}.CreateExecutionStrategy~Microsoft.EntityFrameworkCore.Storage.IExecutionStrategy"),
        Known($"M:{FACADE}.BeginTransaction~{TRANSACTION}"),
        Known($"M:{FACADE}.BeginTransactionAsync({TOKEN})~System.Threading.Tasks.Task{{{TRANSACTION}}}"),
        Known($"M:{FACADE}.get_CurrentTransaction~{TRANSACTION}"),
        Known($"M:{TRANSACTION}.get_TransactionId~System.Guid"),
        Known($"M:{TRANSACTION}.Commit"),
        Known($"M:{TRANSACTION}.CommitAsync({TOKEN})~System.Threading.Tasks.Task"),
        Known($"M:{TRANSACTION}.Rollback"),
        Known($"M:{TRANSACTION}.RollbackAsync({TOKEN})~System.Threading.Tasks.Task"),
        Relational($"M:{RELATIONAL_FACADE}.Migrate({FACADE})"),
        Relational($"M:{RELATIONAL_FACADE}.Migrate({FACADE},System.String)"),
        Relational($"M:{RELATIONAL_FACADE}.MigrateAsync({FACADE},{TOKEN})~System.Threading.Tasks.Task"),
        Relational($"M:{RELATIONAL_FACADE}.MigrateAsync({FACADE},System.String,{TOKEN})~System.Threading.Tasks.Task"),
        Relational($"M:{RELATIONAL_FACADE}.GetDbConnection({FACADE})~System.Data.Common.DbConnection"),
        Relational($"M:{RELATIONAL_FACADE}.UseTransaction({FACADE},System.Data.Common.DbTransaction)~{TRANSACTION}"),
        Relational($"M:{RELATIONAL_FACADE}.UseTransaction({FACADE},System.Data.Common.DbTransaction,System.Guid)~{TRANSACTION}"),
        Relational($"M:{RELATIONAL_FACADE}.BeginTransactionAsync({FACADE},System.Data.IsolationLevel,{TOKEN})~System.Threading.Tasks.Task{{{TRANSACTION}}}"),
        Relational($"M:Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction({TRANSACTION})~System.Data.Common.DbTransaction")
    ];

    /// <summary>A member handed an entity, or a collection of them: a deep read and a write of the argument.</summary>
    private static LibraryMember Entity(string id, string parameter) =>
        new(id, Assemblies, [DeepReadOf(parameter), WriteOf(parameter)]);

    private static LibraryMember Known(string id) => new(id, Assemblies, []);

    private static LibraryMember Relational(string id) => new(id, RelationalAssemblies, []);
}
