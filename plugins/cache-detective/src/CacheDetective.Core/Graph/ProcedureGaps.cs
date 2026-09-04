namespace CacheDetective.Graph;

/// <summary>A call into a stored procedure whose dependencies the graph does not know, and why.
/// <paramref name="Caller"/> is the handler that made the call, when the call was made from code: a gap
/// weakens the confidence of a finding whose chain runs through that handler, exactly as a stored
/// <c>unresolved</c> of the same kind does.</summary>
public sealed record ProcedureGap(string Procedure, Handler? Caller, Unresolved Unresolved);

/// <summary>
/// The two distinguishable reasons a <see cref="StoredProcedure"/> vertex has no outgoing edges.
/// <para>
/// Both are derived on query and never stored. An ordinary <c>unresolved</c> row pins a place in source
/// that would not reduce, and storing it is correct because that place does not change. These two are not
/// about a place but about a gap in the whole graph, and which of them holds depends on whether a database
/// is indexed <em>now</em> — and the order of <c>index_solution</c> and <c>index_database</c> is not
/// fixed. A row saying "the database is not indexed", written while indexing a solution, would survive a
/// later <c>index_database</c> — <see cref="CacheGraph.ReplaceDatabase"/> removes only what belongs to the
/// database — and would then be a lie.
/// </para>
/// </summary>
public static class ProcedureGaps
{
    public static IReadOnlyList<ProcedureGap> Derive(CacheGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var edges = graph.Edges.ToArray();
        var databases = GetIndexedDatabases(graph);
        var answered = edges.Select(edge => edge.From).OfType<StoredProcedure>().Select(procedure => procedure.Name).ToHashSet(StringComparer.Ordinal);
        var recorded = new HashSet<string>(StringComparer.Ordinal);
        var gaps = new List<ProcedureGap>();

        foreach (var call in edges.OfType<Calls>())
        {
            if (call.To is not StoredProcedure procedure || answered.Contains(procedure.Name))
            {
                continue;
            }

            var reason = GetReason(procedure, databases);
            if (reason is null)
            {
                continue;
            }

            var site = call.Evidence.FirstOrDefault() ?? Evidence.InDatabase(procedure.Name, procedure.Database);
            var caller = call.From as Handler;
            var identity = string.Join('|', caller?.Symbol, site.Describe(), procedure.Name);
            if (!recorded.Add(identity))
            {
                continue;
            }

            // The id is reserved from the graph's session sequence, not computed from the rows stored so
            // far: a row derived now must keep its id when later indexing stores more rows, and must
            // never be handed the id one of those rows will take.
            gaps.Add(new ProcedureGap(procedure.Name, caller,
                                      new Unresolved(graph.GetDerivedUnresolvedId(identity), UnresolvedKind.Sql, caller?.Solution, site, procedure.Name,
                                                     reason)));
        }

        return gaps;
    }

    /// <summary>Nothing, when the graph has no database in it; otherwise the name of the procedure and of
    /// the database whose catalogue does not hold it. A procedure the catalogue <em>did</em> answer for is
    /// no gap even with no edges: it touches nothing the catalogue can see, and anything it hides behind
    /// dynamic SQL was recorded while indexing. It is told apart by carrying a database, which it does
    /// because <see cref="CacheGraph.AddStoredProcedure"/> records every procedure the catalogue listed —
    /// edges or none.</summary>
    private static string? GetReason(StoredProcedure procedure, IReadOnlyList<string> databases)
    {
        if (databases.Count == 0)
        {
            return $"The dependencies of stored procedure '{procedure.Name}' are unknown: no database " +
                   "is indexed. Run index_database to learn what it reads and writes.";
        }

        return procedure.Database is null
                   ? $"Stored procedure '{procedure.Name}' is not in the catalogue of database " +
                     $"'{string.Join("', '", databases)}', so what it reads and writes is unknown."
                   : null;
    }

    /// <summary>The databases the graph has been told about. Only the catalogue half creates a view, a
    /// trigger, or a procedure that knows which database it lives in, so these are its footprint — a
    /// table's database is not a witness, because the code half stamps one on every table it maps.</summary>
    private static IReadOnlyList<string> GetIndexedDatabases(CacheGraph graph) =>
        graph.StoredProcedures.Select(procedure => procedure.Database)
             .Concat(graph.Views.Select(view => view.Database))
             .Concat(graph.Triggers.Select(trigger => trigger.Database))
             .OfType<string>()
             .Distinct(StringComparer.Ordinal)
             .Order(StringComparer.Ordinal)
             .ToArray();
}
