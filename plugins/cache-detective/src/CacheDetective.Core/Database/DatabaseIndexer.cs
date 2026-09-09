using System.Data.Common;
using System.Text.RegularExpressions;
using CacheDetective.Graph;

namespace CacheDetective.Database;

/// <summary>What one pass over a catalogue produced: the graph of that database, and the objects the
/// catalogue refused to answer for — which the caller reports rather than leaving to be inferred from a
/// reason string.</summary>
public sealed record DatabaseIndexResult(CacheGraph Graph, IReadOnlyList<string> UnresolvableObjects);

/// <summary>
/// Reads one database's catalogue into a graph: its procedures, views and triggers, what each of them
/// reads and writes, and which procedures call which. It is handed an open connection and a database
/// name and nothing else — connection strings, environment variables and the workspace configuration are
/// the CLI's to know; see <c>docs/adr/0002</c> and <c>docs/adr/0008</c>.
/// </summary>
public sealed class DatabaseIndexer
{
    /// <summary>The depth and cycle cut-off of the code call graph, so both halves stop the same way.</summary>
    private const int MAXIMUM_DEPTH = 12;

    /// <summary>The catalogue reports that a write happens and never which operation it was, so every
    /// catalogue write carries all three events — and stays <c>confirmed</c>, because the catalogue is
    /// deterministic. The <c>likely</c> grade belongs to the code half's EF heuristic alone.</summary>
    private static readonly WriteEvent[] EVERY_WRITE_EVENT =
        [WriteEvent.Insert, WriteEvent.Update, WriteEvent.Delete];

    /// <summary><c>sys.dm_sql_referenced_entities</c> cannot see through dynamic SQL, so a body that
    /// builds it must be recorded rather than quietly reported as touching nothing.</summary>
    private static readonly Regex DYNAMIC_SQL = new(
        @"\bsp_executesql\b|\bexec(?:ute)?\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task<DatabaseIndexResult> IndexAsync(DbConnection connection, string database,
                                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        var graph = new CacheGraph();
        var unresolvable = new List<string>();
        var catalogue = new CatalogueReader(connection);
        var procedures = await catalogue.ReadProceduresAsync(cancellationToken);
        var views = await catalogue.ReadViewsAsync(cancellationToken);
        var triggers = await catalogue.ReadTriggersAsync(cancellationToken);
        var calls = await ReadProcedureCallsAsync(catalogue, procedures, cancellationToken);
        graph.AddIndexedDatabase(database);

        foreach (var view in views)
        {
            graph.AddView(database, new View(view.Schema, view.Name, database));
        }

        // A login without VIEW DEFINITION at database scope reads an empty
        // sys.sql_expression_dependencies and loses every procedure-to-procedure call without an error
        // anywhere. Reporting nothing there would be the silent skip this project refuses, so the gap is
        // recorded as what it is: the graph does not know, and it says why.
        if (procedures.Count > 0 && !await catalogue.CanSeeDependenciesAsync(cancellationToken))
        {
            graph.AddUnresolved(UnresolvedKind.Sql, solution: null,
                Evidence.InDatabase(database, database), database,
                $"Calls between procedures are unknown: reading them needs VIEW DEFINITION on database " +
                $"'{database}', and this login does not hold it. Granting it on a schema is not enough — " +
                "sys.sql_expression_dependencies then returns no rows at all.");
        }

        foreach (var view in views)
        {
            await AddReferencesAsync(graph, catalogue, database,
                new View(view.Schema, view.Name, database), view.QualifiedName, unresolvable,
                cancellationToken);
        }

        foreach (var trigger in triggers)
        {
            var host = new Table(trigger.TableSchema, trigger.TableName, database);
            var vertex = new Trigger(trigger.Schema, trigger.Name, host.Name, trigger.Events, database);
            graph.AddEdge(new Fires(host, vertex, Confidence.Confirmed,
                [Evidence.InDatabase(trigger.QualifiedName, database)]));
            await AddReferencesAsync(graph, catalogue, database, vertex, trigger.QualifiedName,
                unresolvable, cancellationToken);
        }

        // Every procedure the catalogue listed becomes a vertex before the walk starts, whether or not
        // it turns out to have edges. One whose body is all dynamic SQL references nothing statically,
        // so it would otherwise never enter the graph — and the query layer would then report it as a
        // procedure this database does not hold, in the same breath as the dynamic-SQL row naming it.
        foreach (var procedure in procedures)
        {
            graph.AddStoredProcedure(database, procedure.ToVertex(database));
        }

        var shallowestDepth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var indexed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitted = new HashSet<(string From, string To)>();

        foreach (var procedure in procedures)
        {
            await WalkAsync(procedure, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        return new DatabaseIndexResult(graph, unresolvable);

        async Task WalkAsync(CatalogueProcedure procedure, int depth, HashSet<string> activePath)
        {
            var name = procedure.QualifiedName;
            if (activePath.Contains(name))
            {
                return;
            }

            if (shallowestDepth.TryGetValue(name, out var previousDepth) && previousDepth <= depth)
            {
                return;
            }

            shallowestDepth[name] = depth;
            activePath.Add(name);

            if (indexed.Add(name))
            {
                await IndexProcedureAsync(graph, catalogue, database, procedure, unresolvable,
                    cancellationToken);
            }

            if (depth == MAXIMUM_DEPTH || !calls.TryGetValue(name, out var targets))
            {
                return;
            }

            foreach (var target in targets)
            {
                if (emitted.Add((name, target.QualifiedName)))
                {
                    graph.AddEdge(new Calls(procedure.ToVertex(database), target.ToVertex(database),
                        Confidence.Confirmed, [Evidence.InDatabase(name, database)]));
                }

                if (!activePath.Contains(target.QualifiedName))
                {
                    await WalkAsync(target, depth + 1,
                        new HashSet<string>(activePath, StringComparer.OrdinalIgnoreCase));
                }
            }
        }
    }

    private static async Task IndexProcedureAsync(CacheGraph graph, CatalogueReader catalogue, string database,
                                                   CatalogueProcedure procedure, List<string> unresolvable,
                                                   CancellationToken cancellationToken)
    {
        await AddReferencesAsync(graph, catalogue, database, procedure.ToVertex(database),
            procedure.QualifiedName, unresolvable, cancellationToken);

        if (procedure.Definition is null)
        {
            return;
        }

        var match = DYNAMIC_SQL.Match(procedure.Definition);
        if (match.Success)
        {
            graph.AddUnresolved(UnresolvedKind.Sql, solution: null,
                Evidence.InDatabase(procedure.QualifiedName, database),
                GetExcerpt(procedure.Definition, match.Index),
                "The procedure builds dynamic SQL, which the catalogue cannot follow; what it reads and "
                + "writes there is unknown.");
        }
    }

    /// <summary>Reads what one object selects from and updates. The same dynamic management function
    /// answers for procedures, views and triggers alike, and it is the only source that separates a read
    /// from a write — which is why a trigger's write can be classified at all.</summary>
    private static async Task AddReferencesAsync(CacheGraph graph, CatalogueReader catalogue, string database,
                                                  ReadSource source, string qualifiedName,
                                                  List<string> unresolvable,
                                                  CancellationToken cancellationToken)
    {
        var evidence = Evidence.InDatabase(qualifiedName, database);
        IReadOnlyList<CatalogueReference> references;
        try
        {
            references = await catalogue.ReadReferencedEntitiesAsync(qualifiedName, cancellationToken);
        }
        catch (DbException error)
        {
            unresolvable.Add(qualifiedName);
            graph.AddUnresolved(UnresolvedKind.Sql, solution: null, evidence, qualifiedName,
                $"The catalogue could not resolve what this object references, which usually means it "
                + $"names an object that does not exist: {error.Message}");
            return;
        }

        foreach (var reference in references)
        {
            var table = new Table(reference.Schema, reference.Name, database);
            if (reference.IsSelected)
            {
                graph.AddEdge(new Reads(source, table, Confidence.Confirmed, [evidence]));
            }

            // A view is a read source and never a write source, so an updatable view's write is dropped
            // rather than modelled; see the Table and View rows of CONTEXT.md.
            if (reference.IsUpdated && source is WriteSource writeSource)
            {
                graph.AddEdge(new Writes(writeSource, table, Confidence.Confirmed, [evidence],
                    EVERY_WRITE_EVENT));
            }
        }
    }

    /// <summary><c>sys.sql_expression_dependencies</c> answers one question only: which procedure calls
    /// which. It cannot replace the read/write split above, because it does not carry one.</summary>
    private static async Task<Dictionary<string, CatalogueProcedure[]>> ReadProcedureCallsAsync(
        CatalogueReader catalogue, IReadOnlyList<CatalogueProcedure> procedures,
        CancellationToken cancellationToken)
    {
        var byName = procedures.ToDictionary(procedure => procedure.QualifiedName,
            StringComparer.OrdinalIgnoreCase);
        var dependencies = await catalogue.ReadProcedureCallsAsync(cancellationToken);

        return dependencies
            .Where(dependency => byName.ContainsKey(dependency.ReferencedName))
            .GroupBy(dependency => dependency.ReferencingName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                          group => group.Select(dependency => byName[dependency.ReferencedName])
                                        .Distinct()
                                        .ToArray(),
                          StringComparer.OrdinalIgnoreCase);
    }

    private static string GetExcerpt(string definition, int index)
    {
        var start = definition.LastIndexOfAny(['\n', '\r'], Math.Min(index, definition.Length - 1)) + 1;
        var end = definition.IndexOfAny(['\n', '\r'], index);
        var line = (end < 0 ? definition[start..] : definition[start..end]).Trim();
        return line.Length > 200 ? line[..200] : line;
    }
}
