namespace PlanForge.Vendors;

/// <summary>
/// Recency parsed out of a model id, shared because two vendors need it for the same reason: the
/// order their ids arrive in is not recency, and the interview is promised "newest first".
/// </summary>
internal static class ModelVersion
{
    /// <summary>
    /// The version is the first run of numeric tokens, each contributing its dot-separated
    /// segments: "claude-opus-4-8" is the two-segment 4.8 written with dashes, not the integer 48,
    /// and "gpt-5.3-codex" carries 5.3 inside one token. Ids without one ("auto") return empty.
    /// </summary>
    public static int[] Segments(string id)
    {
        var segments = new List<int>();
        foreach (var token in id.Split('-'))
        {
            var parts = token.Split('.');
            if (parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit)))
                segments.AddRange(parts.Select(int.Parse));
            else if (segments.Count > 0) break;
        }

        return [.. segments];
    }

    public static IComparer<int[]> Order { get; } = new SegmentOrder();

    private sealed class SegmentOrder : IComparer<int[]>
    {
        public int Compare(int[]? left, int[]? right)
        {
            for (var index = 0; index < Math.Max(left!.Length, right!.Length); index++)
            {
                var difference = Segment(left, index).CompareTo(Segment(right, index));
                if (difference != 0) return difference;
            }

            return 0;
        }

        private static int Segment(int[] version, int index) => index < version.Length ? version[index] : 0;
    }
}
