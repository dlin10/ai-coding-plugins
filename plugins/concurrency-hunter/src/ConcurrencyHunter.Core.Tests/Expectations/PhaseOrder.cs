namespace ConcurrencyHunter.Core.Tests.Expectations;

internal static class PhaseOrder
{
    private const string PHASE_WITH_RUNS = "5d";

    private static readonly string[] Phases = ["0", "1a", "1b", "2", "2b", "3", "4", "4b", "5a", "5b", "5c", "5d", "5e", "5f", "5g", "6", "7", "8"];

    private static readonly string[] Runs = ["A1", "A2", "B1", "A3", "B2", "B2b", "A4", "A5", "A6", "A7", "A8", "A9", "A10", "B3", "B4", "C"];

    /// <summary>Every point of the order: a phase, and right after it the runs of that phase as <c>&lt;phase&gt;-&lt;run&gt;</c>.</summary>
    private static readonly string[] Points =
        [.. Phases.SelectMany(phase => phase == PHASE_WITH_RUNS
                                  ? Runs.Select(run => $"{phase}-{run}").Prepend(phase)
                                  : new[] { phase })];

    /// <summary>Orders two points: a phase sorts before its own runs and after the runs of the phase before it.</summary>
    /// <param name="left">A phase or a run of a phase.</param>
    /// <param name="right">A phase or a run of a phase.</param>
    internal static int Compare(string left, string right) => Index(left).CompareTo(Index(right));

    private static int Index(string point)
    {
        var index = Array.IndexOf(Points, point);
        return index >= 0
            ? index
            : throw new ArgumentOutOfRangeException(nameof(point), point, "Unknown phase or run of a phase.");
    }
}
