using static ConcurrencyHunter.Providers.LibrarySemantics.LibraryEffect;

namespace ConcurrencyHunter.Providers.LibrarySemantics;

/// <summary>LINQ (R2): the <c>Enumerable</c> operators without a delegate or a comparer read every sequence and value they are
/// given deep, at the call, deferred or not; the <c>Queryable</c> operators touch nothing, since an expression tree is data. The
/// operators that take a delegate stay opaque until phase 5e.</summary>
internal static class LinqFamily
{
    private const string ENUMERABLE = "M:System.Linq.Enumerable.";
    private const string QUERYABLE = "M:System.Linq.Queryable.";
    private const string SEQUENCE = "System.Collections.Generic.IEnumerable{``0}";
    private const string QUERY = "System.Linq.IQueryable{``0}";
    private const string PREDICATE = "System.Linq.Expressions.Expression{System.Func{``0,System.Boolean}}";

    private static readonly SupportedAssemblyVersion[] Assemblies = [SupportedAssemblyVersion.Framework("System.Linq")];
    private static readonly SupportedAssemblyVersion[] QueryableAssemblies = [SupportedAssemblyVersion.Framework("System.Linq.Queryable")];

    internal static IEnumerable<LibraryMember> Members =>
    [
        Source($"Any``1({SEQUENCE})~System.Boolean"),
        Source($"Count``1({SEQUENCE})~System.Int32"),
        Source($"LongCount``1({SEQUENCE})~System.Int64"),
        Source($"ToList``1({SEQUENCE})~System.Collections.Generic.List{{``0}}"),
        Source($"ToArray``1({SEQUENCE})~``0[]"),
        Source($"First``1({SEQUENCE})~``0"),
        Source($"FirstOrDefault``1({SEQUENCE})~``0"),
        Source($"Single``1({SEQUENCE})~``0"),
        Source($"SingleOrDefault``1({SEQUENCE})~``0"),
        Source($"Last``1({SEQUENCE})~``0"),
        Source($"LastOrDefault``1({SEQUENCE})~``0"),
        Source($"Skip``1({SEQUENCE},System.Int32)~{SEQUENCE}"),
        Source($"Take``1({SEQUENCE},System.Int32)~{SEQUENCE}"),
        Source($"Take``1({SEQUENCE},System.Range)~{SEQUENCE}"),
        Source($"OfType``1(System.Collections.IEnumerable)~{SEQUENCE}"),
        Source($"Cast``1(System.Collections.IEnumerable)~{SEQUENCE}"),
        Source($"AsEnumerable``1({SEQUENCE})~{SEQUENCE}"),
        Enumerable($"Contains``1({SEQUENCE},``0)~System.Boolean", "source", "value"),
        Enumerable($"SequenceEqual``1({SEQUENCE},{SEQUENCE})~System.Boolean", "first", "second"),
        Enumerable($"Union``1({SEQUENCE},{SEQUENCE})~{SEQUENCE}", "first", "second"),
        Enumerable($"Empty``1~{SEQUENCE}"),
        Queryable($"Where``1({QUERY},{PREDICATE})~{QUERY}"),
        Queryable($"Where``1({QUERY},System.Linq.Expressions.Expression{{System.Func{{``0,System.Int32,System.Boolean}}}})~{QUERY}"),
        Queryable($"Any``1({QUERY})~System.Boolean"),
        Queryable($"Any``1({QUERY},{PREDICATE})~System.Boolean"),
        Queryable($"Skip``1({QUERY},System.Int32)~{QUERY}"),
        Queryable($"Take``1({QUERY},System.Int32)~{QUERY}"),
        Queryable($"Take``1({QUERY},System.Range)~{QUERY}"),
        Queryable($"OrderBy``2({QUERY},System.Linq.Expressions.Expression{{System.Func{{``0,``1}}}})~System.Linq.IOrderedQueryable{{``0}}"),
        Queryable($"OrderBy``2({QUERY},System.Linq.Expressions.Expression{{System.Func{{``0,``1}}}},System.Collections.Generic.IComparer{{``1}})" +
                  "~System.Linq.IOrderedQueryable{``0}"),
        Queryable($"Single``1({QUERY})~``0"),
        Queryable($"Single``1({QUERY},{PREDICATE})~``0"),
        Queryable($"SingleOrDefault``1({QUERY})~``0"),
        Queryable($"SingleOrDefault``1({QUERY},``0)~``0"),
        Queryable($"SingleOrDefault``1({QUERY},{PREDICATE})~``0"),
        Queryable($"SingleOrDefault``1({QUERY},{PREDICATE},``0)~``0")
    ];

    private static LibraryMember Source(string signature) => Enumerable(signature, "source");

    private static LibraryMember Enumerable(string signature, params string[] read) =>
        new(ENUMERABLE + signature, Assemblies, read.Select(parameter => DeepReadOf(parameter)).ToArray());

    private static LibraryMember Queryable(string signature) => new(QUERYABLE + signature, QueryableAssemblies, []);
}
