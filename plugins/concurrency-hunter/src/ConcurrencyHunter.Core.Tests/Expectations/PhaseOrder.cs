namespace ConcurrencyHunter.Core.Tests.Expectations;

internal static class PhaseOrder
{
    private static readonly string[] Phases = ["0", "1a", "1b", "2", "2b", "3", "4", "4b", "5a", "5b", "5c", "5d", "5g", "5h", "5e", "5f", "6", "7", "8"];

    private static readonly Dictionary<string, string[]> Runs = new()
    {
        ["5d"] = ["A1", "A2", "B1", "A3", "B2", "B2b", "A4", "A5", "A6", "A7", "A8", "A9", "A10", "B3", "B3c", "B4", "B4b", "B4c"],
        ["5h"] = ["C"],
    };

    /// <summary>Every point of the order: a phase, and right after it the runs of that phase as <c>&lt;phase&gt;-&lt;run&gt;</c>.</summary>
    private static readonly string[] Points =
        [.. Phases.SelectMany(phase => Runs.GetValueOrDefault(phase, []).Select(run => $"{phase}-{run}").Prepend(phase))];

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
