using CacheDetective.Serialization;
using Common.Mcp;
using Common.Roslyn;
using System.Text.Json;

namespace CacheDetective.Mcp;

/// <summary>How a response is shaped to fit the byte budget: what the shell around the diagnostics may
/// weigh and what is left for a page of them, delegating to <see cref="DiagnosticFragments"/> for how a
/// message too long to travel whole is cut into pieces that each fit. Nothing here reads session state —
/// every answer is a function of the values passed in, so the budgets can be reasoned about, and tested,
/// without a workspace.</summary>
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

    internal const int MaximumShellBytes = DiagnosticFragments.MaximumShellBytes;

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
        return DiagnosticFragments.Describe(pending.Select(item => ("Warning", $"{item.List} continued: {string.Join(", ", item.Hidden)}"))
                                                    .ToArray(),
                                            used);
    }

    internal static ListEnvelope<WorkspaceDiagnosticResult> Empty() =>
        ResponseEnvelope.Create(Array.Empty<WorkspaceDiagnosticResult>(), null,
                                CacheDetectiveJsonContext.Default.ListEnvelopeWorkspaceDiagnosticResult);

    internal static int Weight(IndexSolutionResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, CacheDetectiveJsonContext.Default.IndexSolutionResult).Length;

    internal static ListEnvelope<SolutionStatus> PageSolutions(IReadOnlyList<SolutionStatus> solutions, PageArguments? page) =>
        ResponseEnvelope.Create(solutions,
                                page,
                                CacheDetectiveJsonContext.Default.ListEnvelopeSolutionStatus);

    internal static IReadOnlyList<WorkspaceDiagnosticResult> Describe(IReadOnlyList<Microsoft.CodeAnalysis.WorkspaceDiagnostic> diagnostics,
                                                                       int numberedFrom = 0) =>
        WorkspaceDiagnostics.Describe(diagnostics, numberedFrom);

    /// <param name="reserve">How much the result carrying this envelope weighs around it. Without it the
    /// envelope fills the whole budget on its own and whatever wraps it pushes the response over.</param>
    internal static ListEnvelope<WorkspaceDiagnosticResult> PageDiagnostics(IReadOnlyList<WorkspaceDiagnosticResult> diagnostics,
                                                                             PageArguments? page, int reserve = 0) =>
        DiagnosticFragments.Page(diagnostics, page, reserve);
}
