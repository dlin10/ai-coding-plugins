using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>One member's line of the CoreLib generation snapshot: the parts of its answer that stay the same from run to run, so that
/// a difference between two snapshots is a difference of the generator or of the implementation it read.</summary>
/// <param name="Member">The member's declaration id.</param>
/// <param name="Outcome">The refusal reason when nothing was classified, <c>model</c> when the member has a model, else the model
/// reason; <c>timeout</c> or <c>threw</c> when the generation gave no answer.</param>
/// <param name="Causes">The codes of the answer's causes, sorted.</param>
/// <param name="Model">The model entry as compact JSON, or empty.</param>
internal sealed record GenerationSnapshotRow(string Member, string Outcome, IReadOnlyList<string> Causes, string Model)
{
    /// <summary>The outcome of a member with a model.</summary>
    public const string MODEL = "model";

    /// <summary>The model's JSON keeps a declaration id's characters, as the answer document does, rather than escaping its backticks.</summary>
    private static readonly JsonSerializerOptions JSON = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The row of one answer.</summary>
    /// <param name="answer">The answer.</param>
    public static GenerationSnapshotRow Of(GeneratedAnswer answer) =>
        new(answer.Member, answer.Reason ?? (answer.Model is null ? answer.ModelReason ?? "-" : MODEL), answer.Causes.Select(cause => cause.Code).ToArray(),
            answer.Model is null ? "" : ModelEntryWriter.Write(answer.Model).ToJsonString(JSON));

    /// <summary>The row as one tab-separated line.</summary>
    public string Line => string.Join('\t', Member, Outcome, string.Join(',', Causes), Model);

    /// <summary>Reads a row from its line.</summary>
    /// <param name="line">The line.</param>
    /// <exception cref="FormatException">The line does not have four tab-separated parts.</exception>
    public static GenerationSnapshotRow Parse(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length != 4)
            throw new FormatException($"A snapshot line has four tab-separated parts: {line}");
        return new GenerationSnapshotRow(parts[0], parts[1], parts[2].Length == 0 ? [] : parts[2].Split(','), parts[3]);
    }
}

/// <summary>A CoreLib generation snapshot: the implementation it read and one row per member.</summary>
/// <param name="Implementation">The implementation assembly, as the header line names it.</param>
/// <param name="Rows">The rows, sorted by member.</param>
internal sealed record GenerationSnapshot(string Implementation, IReadOnlyList<GenerationSnapshotRow> Rows)
{
    private const string HEADER = "# ";

    /// <summary>The snapshot's lines: the header, then the rows sorted by member.</summary>
    public IEnumerable<string> Lines => Rows.OrderBy(row => row.Member, StringComparer.Ordinal).Select(row => row.Line).Prepend(HEADER + Implementation);

    /// <summary>Reads a snapshot from its lines.</summary>
    /// <param name="lines">The lines: a header line starting with <c>#</c>, then one row per line.</param>
    /// <exception cref="FormatException">A row does not have four parts, or a member stands twice.</exception>
    public static GenerationSnapshot Parse(IEnumerable<string> lines)
    {
        var implementation = "";
        var rows = new List<GenerationSnapshotRow>();
        foreach (var line in lines.Where(line => line.Length != 0))
        {
            if (line.StartsWith(HEADER, StringComparison.Ordinal))
                implementation = line[HEADER.Length..];
            else
                rows.Add(GenerationSnapshotRow.Parse(line));
        }
        if (rows.GroupBy(row => row.Member, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice)
            throw new FormatException($"{twice.Key} stands twice in the snapshot");
        return new GenerationSnapshot(implementation, rows.OrderBy(row => row.Member, StringComparer.Ordinal).ToArray());
    }
}

/// <summary>The generation of every public member of <c>System.Private.CoreLib</c>, and the comparison of two of its snapshots.</summary>
internal static class CoreLibGeneration
{
    /// <summary>The declaration ids of the methods a caller outside the assembly could call by the compilation's declarations, sorted:
    /// the method and every type around it public, protected or protected internal. The compilation of an opened copy declares what
    /// the copy opened as well.</summary>
    /// <param name="compilation">The library compilation.</param>
    public static IReadOnlyList<string> PublicMembers(Compilation compilation)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<INamespaceOrTypeSymbol>();
        pending.Push(compilation.Assembly.GlobalNamespace);
        while (pending.TryPop(out var current))
        {
            foreach (var member in current.GetMembers())
            {
                switch (member)
                {
                    case INamespaceSymbol @namespace:
                        pending.Push(@namespace);
                        break;
                    case INamedTypeSymbol type when Visible(type.DeclaredAccessibility):
                        pending.Push(type);
                        break;
                    case IMethodSymbol method when current is INamedTypeSymbol && Visible(method.DeclaredAccessibility) &&
                                                   method.GetDocumentationCommentId() is { } id:
                        ids.Add(id);
                        break;
                }
            }
        }
        return ids.ToArray();
    }

    /// <summary>What differs between two snapshots, in report lines; empty when they are the same. The implementation comes first,
    /// since a different one explains differences the generator did not make.</summary>
    /// <param name="before">The earlier snapshot.</param>
    /// <param name="after">The later snapshot.</param>
    /// <param name="examples">How many members each line names at most.</param>
    public static IReadOnlyList<string> Compare(GenerationSnapshot before, GenerationSnapshot after, int examples)
    {
        var report = new List<string>();
        if (before.Implementation != after.Implementation)
            report.Add($"implementation: {before.Implementation} -> {after.Implementation}");

        var earlier = before.Rows.ToDictionary(row => row.Member, StringComparer.Ordinal);
        var later = after.Rows.ToDictionary(row => row.Member, StringComparer.Ordinal);
        AddMembers(report, "members gone", earlier.Keys.Where(member => !later.ContainsKey(member)), examples);
        AddMembers(report, "members new", later.Keys.Where(member => !earlier.ContainsKey(member)), examples);

        AddCounts(report, "outcome", Counts(before.Rows, row => [row.Outcome]), Counts(after.Rows, row => [row.Outcome]));

        var both = earlier.Keys.Where(later.ContainsKey).Order(StringComparer.Ordinal).ToArray();
        foreach (var transition in both.Where(member => earlier[member].Outcome != later[member].Outcome)
                                       .GroupBy(member => $"{earlier[member].Outcome} -> {later[member].Outcome}", StringComparer.Ordinal)
                                       .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            AddMembers(report, transition.Key, transition, examples);
        }
        AddMembers(report, "models changed", both.Where(member => earlier[member].Outcome == GenerationSnapshotRow.MODEL &&
                                                                  later[member].Outcome == GenerationSnapshotRow.MODEL &&
                                                                  earlier[member].Model != later[member].Model), examples);

        AddCounts(report, "cause", Counts(before.Rows, row => row.Causes), Counts(after.Rows, row => row.Causes));
        AddMembers(report, "causes changed, outcome the same",
                   both.Where(member => earlier[member].Outcome == later[member].Outcome &&
                                        !earlier[member].Causes.SequenceEqual(later[member].Causes, StringComparer.Ordinal)), examples);
        return report;
    }

    /// <summary>How many rows carry each value.</summary>
    /// <param name="rows">The rows.</param>
    /// <param name="values">The values of one row.</param>
    private static Dictionary<string, int> Counts(IEnumerable<GenerationSnapshotRow> rows, Func<GenerationSnapshotRow, IEnumerable<string>> values) =>
        rows.SelectMany(values).GroupBy(value => value, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    /// <summary>Adds one line for each value whose count differs, in value order.</summary>
    /// <param name="report">The report.</param>
    /// <param name="label">What the values are.</param>
    /// <param name="before">The counts in the earlier snapshot.</param>
    /// <param name="after">The counts in the later snapshot.</param>
    private static void AddCounts(List<string> report, string label, Dictionary<string, int> before, Dictionary<string, int> after)
    {
        foreach (var value in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var (was, now) = (before.GetValueOrDefault(value), after.GetValueOrDefault(value));
            if (was != now)
                report.Add($"{label} {value}: {was} -> {now}");
        }
    }

    /// <summary>Adds one line for a set of members, when there are any: their count and the first of them.</summary>
    /// <param name="report">The report.</param>
    /// <param name="label">What the members have in common.</param>
    /// <param name="members">The members.</param>
    /// <param name="examples">How many members the line names at most.</param>
    private static void AddMembers(List<string> report, string label, IEnumerable<string> members, int examples)
    {
        var all = members.Order(StringComparer.Ordinal).ToArray();
        if (all.Length != 0)
            report.Add($"{label}: {all.Length}, e.g. {string.Join(", ", all.Take(examples))}");
    }

    /// <summary>Whether a caller outside the assembly can reach a declaration of this accessibility.</summary>
    /// <param name="accessibility">The declared accessibility.</param>
    private static bool Visible(Accessibility accessibility) =>
        accessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;
}
