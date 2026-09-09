using CacheDetective.Serialization;
using System.Text.Json;

namespace CacheDetective.Mcp;

/// <summary>How a response is shaped to fit the byte budget: what the shell around the diagnostics may
/// weigh, what is left for a page of them, and how a message too long to travel whole is cut into fragments
/// that each fit. Nothing here reads session state — every answer is a function of the values passed in, so
/// the budgets can be reasoned about, and tested, without a workspace.</summary>
internal static class ResponseFitting
{
    /// <summary>
    /// The longest prefix of a list of project names the shell can show. Decided from the shell alone, so
    /// every page of one index shows the same names and reports the same hidden count.
    /// </summary>
    internal static IReadOnlyList<string> Fitted(IReadOnlyList<string> names,
                                                 Func<IReadOnlyList<string>, IndexSolutionResult> shell)
    {
        if (Weight(shell(names)) <= MaximumShellBytes)
            return names;

        for (var kept = names.Count / 2; kept > 0; kept /= 2)
        {
            var candidate = names.Take(kept).ToArray();
            if (Weight(shell(candidate)) <= MaximumShellBytes)
                return candidate;
        }

        return [];
    }

    /// <summary>
    /// The one budget everything else is derived from: the most the result around the diagnostics may
    /// weigh. What is left of <see cref="ResponseEnvelope.MaximumSerializedBytes"/> after it is what a page
    /// of diagnostics has to live in, and <see cref="DiagnosticFragmentBytes"/> is cut from that same
    /// remainder — so a fragment always fits beside a shell.
    /// <para>The two used to be set apart from each other: the shell was allowed to keep all but 2048
    /// bytes while a fragment could be 4096, so with enough project names no fragment fitted at all and
    /// every page of diagnostics came back empty.</para>
    /// </summary>
    internal const int MaximumShellBytes = ResponseEnvelope.MaximumSerializedBytes / 2;

    /// <summary>
    /// What the shell may weigh once the error is in it and before any list is. The rest of the budget is
    /// what the three project lists are fitted into, so an error can never crowd them out entirely — and,
    /// more to the point, can never leave the shell heavier than the whole budget with every list already
    /// empty, which is the state <see cref="Fitted"/> has no answer for.
    /// </summary>
    private const int MAXIMUM_ERRORED_SHELL_BYTES = MaximumShellBytes / 2;

    private const string ERROR_TRUNCATION_MARKER = " … (error truncated)";

    /// <summary>
    /// The error, cut to what the shell can carry. The measure is the serialized weight of the shell around
    /// it, not the error's character count: 1024 characters of Cyrillic escape to roughly 6144 bytes of
    /// JSON, so a count that looked modest left the shell heavier than its whole budget, the lists were
    /// emptied for nothing, and no diagnostic fragment could fit beside it.
    /// <para>It is the one part of the shell that is not a list and so cannot be carried into the
    /// diagnostics. Cutting is the only thing that can be done with it, and the cut never lands between the
    /// halves of a surrogate pair.</para>
    /// </summary>
    internal static string? FittedError(string? error, Func<string?, IndexSolutionResult> shell) =>
        ResponseText.Fitted(error, candidate => Weight(shell(candidate)), MAXIMUM_ERRORED_SHELL_BYTES,
                            ERROR_TRUNCATION_MARKER);

    /// <summary>
    /// The names that did not fit, as diagnostics of their own — one per list, each labelled with the list
    /// it continues. Their ids continue after the last real diagnostic's, because SKILL.md tells the agent
    /// to rejoin a long message by id and part: sharing <c>d:1</c> with a real diagnostic would splice two
    /// different messages into one, and sharing one between two lists would splice those.
    /// </summary>
    internal static IReadOnlyList<WorkspaceDiagnosticResult> Carried(IReadOnlyList<WorkspaceDiagnosticResult> real,
                                                                      params (string List, IReadOnlyList<string> Hidden)[] carried)
    {
        var pending = carried.Where(item => item.Hidden.Count > 0).ToArray();
        if (pending.Length == 0)
            return [];

        var used = real.Select(item => item.Id)
                       .Where(id => id.StartsWith("d:", StringComparison.Ordinal))
                       .Select(id => int.TryParse(id[2..], out var number) ? number : 0)
                       .DefaultIfEmpty(0)
                       .Max();
        return Describe(pending.Select(item => (Microsoft.CodeAnalysis.WorkspaceDiagnostic)
                                           new CarriedProjects(item.List, string.Join(", ", item.Hidden)))
                               .ToArray(),
                        used);
    }

    internal static ListEnvelope<WorkspaceDiagnosticResult> Empty() =>
        ResponseEnvelope.Create(Array.Empty<WorkspaceDiagnosticResult>(), null,
                                CacheDetectiveJsonContext.Default.ListEnvelopeWorkspaceDiagnosticResult);

    /// <summary>The names that did not fit, carried as a diagnostic so that the fragment machinery pages
    /// them like any other long message.</summary>
    private sealed class CarriedProjects(string list, string names)
        : Microsoft.CodeAnalysis.WorkspaceDiagnostic(Microsoft.CodeAnalysis.WorkspaceDiagnosticKind.Warning,
                                                     $"{list} continued: {names}");

    internal static int Weight(IndexSolutionResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, CacheDetectiveJsonContext.Default.IndexSolutionResult).Length;

    internal static ListEnvelope<SolutionStatus> PageSolutions(IReadOnlyList<SolutionStatus> solutions, PageArguments? page) =>
        ResponseEnvelope.Create(solutions,
                                page,
                                CacheDetectiveJsonContext.Default.ListEnvelopeSolutionStatus);

    /// <summary>
    /// What the envelope around a single fragment costs, measured rather than estimated — and measured at
    /// its worst, with the counts and the notice <see cref="ResponseEnvelope"/> writes when it has had to
    /// reduce a page. It is declared before the budget it feeds, because static initialisers run in the
    /// order they are written.
    /// </summary>
    private static readonly int ENVELOPE_OVERHEAD = JsonSerializer.SerializeToUtf8Bytes(new ListEnvelope<WorkspaceDiagnosticResult>(int.MaxValue, int.MaxValue, int.MaxValue, [],
                                                                                            "Page size was reduced to stay under the response limit."),
                                                                                       CacheDetectiveJsonContext.Default.ListEnvelopeWorkspaceDiagnosticResult).Length;

    /// <summary>
    /// How much a fragment of one diagnostic message may weigh once serialized. It is what is left of the
    /// response limit once the heaviest shell and the envelope around the fragment have both been paid
    /// for, so a fragment that passes this fits on a page beside any shell the result can produce. Setting
    /// it independently of the shell's budget is what made every page come back empty: a 4096-byte
    /// fragment cannot go on a page a 6144-byte shell left 2048 bytes of.
    /// <para>The measure is bytes of JSON rather than UTF-16 characters because the two are not the same
    /// size: a Cyrillic message costs two bytes a character before escaping and six after (<c>\uXXXX</c>),
    /// so 1500 characters could reach about 9000 bytes and <see cref="ResponseEnvelope"/> would answer
    /// with an empty page and a notice — no answer at all.</para>
    /// </summary>
    private static readonly int DIAGNOSTIC_FRAGMENT_BYTES =
        ResponseEnvelope.MaximumSerializedBytes - MaximumShellBytes - ENVELOPE_OVERHEAD;

    /// <summary>The largest fragment tried first. Cutting is by bytes, so the character count only bounds
    /// the search.</summary>
    private const int DIAGNOSTIC_FRAGMENT_CHARACTERS = 1500;

    internal static IReadOnlyList<WorkspaceDiagnosticResult> Describe(IReadOnlyList<Microsoft.CodeAnalysis.WorkspaceDiagnostic> diagnostics,
                                                                       int numberedFrom = 0)
    {
        var mapped = new List<WorkspaceDiagnosticResult>();
        for (var index = 0; index < diagnostics.Count; index++)
        {
            var diagnostic = diagnostics[index];
            var id = $"d:{numberedFrom + index + 1}";
            var kind = diagnostic.Kind.ToString();
            var fragments = Fragments(diagnostic.Message ?? string.Empty, id, kind);
            for (var part = 0; part < fragments.Count; part++)
                mapped.Add(new WorkspaceDiagnosticResult(id, kind, fragments[part], part + 1, fragments.Count));
        }

        return mapped;
    }

    /// <summary>
    /// Cuts a message into pieces each of which fits when serialized. A cut never lands between the two
    /// halves of a surrogate pair, which would turn one character into two broken ones and make the
    /// reassembled text differ from the original.
    /// </summary>
    private static IReadOnlyList<string> Fragments(string message, string id, string kind)
    {
        if (message.Length == 0)
            return [string.Empty];

        var fragments = new List<string>();
        var start = 0;
        while (start < message.Length)
        {
            var length = Math.Min(DIAGNOSTIC_FRAGMENT_CHARACTERS, message.Length - start);
            length = WithoutSplitSurrogate(message, start, length);
            while (length > 1 && SerializedSize(message.Substring(start, length), id, kind) > DIAGNOSTIC_FRAGMENT_BYTES)
                length = WithoutSplitSurrogate(message, start, Math.Max(1, length / 2));

            fragments.Add(message.Substring(start, length));
            start += length;
        }

        return fragments;
    }

    private static int WithoutSplitSurrogate(string message, int start, int length) =>
        ResponseText.WithoutSplitSurrogate(message, start, length);

    private static int SerializedSize(string fragment, string id, string kind) =>
        JsonSerializer.SerializeToUtf8Bytes(new WorkspaceDiagnosticResult(id, kind, fragment, 1, 1),
                                            CacheDetectiveJsonContext.Default.WorkspaceDiagnosticResult).Length;

    /// <param name="reserve">How much the result carrying this envelope weighs around it. Without it the
    /// envelope fills the whole budget on its own and whatever wraps it pushes the response over.</param>
    internal static ListEnvelope<WorkspaceDiagnosticResult> PageDiagnostics(IReadOnlyList<WorkspaceDiagnosticResult> diagnostics,
                                                                             PageArguments? page, int reserve = 0) =>
        ResponseEnvelope.Create(diagnostics, page, CacheDetectiveJsonContext.Default.ListEnvelopeWorkspaceDiagnosticResult, reserve);
}
