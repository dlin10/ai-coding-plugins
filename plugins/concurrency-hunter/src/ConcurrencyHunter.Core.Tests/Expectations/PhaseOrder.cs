namespace ConcurrencyHunter.Core.Tests.Expectations;

internal static class PhaseOrder
{
    private static readonly string[] Phases = ["0", "1a", "1b", "2", "2b", "3", "4", "4b", "5a", "5b", "5c", "5d", "5e", "6", "7", "8"];

    internal static int Compare(string left, string right) => Index(left).CompareTo(Index(right));

    private static int Index(string phase)
    {
        var index = Array.IndexOf(Phases, phase);
        return index >= 0
            ? index
            : throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown phase.");
    }
}
