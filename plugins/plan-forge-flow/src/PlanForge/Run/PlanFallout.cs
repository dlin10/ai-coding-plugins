namespace PlanForge.Run;

/// <summary>
/// The orchestrator's labels for the new findings of the plan-review round before this call: which of
/// them the plan's own revisions caused, and which are older gaps. Every new finding of that round
/// is named under exactly one label, and <see cref="Check"/> refuses a call whose labels leave one
/// out. See docs/adr/0049.
/// </summary>
/// <param name="Own">Something an earlier revision wrote is wrong: the rule it keyed on, or anything else it changed.</param>
/// <param name="Sibling">A place that answers the same question as an earlier revision was left behind.</param>
/// <param name="SameClass">An older instance of a class an earlier revision covered only where a finding named it.</param>
/// <param name="Older">Anything else the review predates.</param>
internal sealed record PlanFallout(IReadOnlyList<string>? Own = null,
                                   IReadOnlyList<string>? Sibling = null,
                                   IReadOnlyList<string>? SameClass = null,
                                   IReadOnlyList<string>? Older = null)
{
    /// <summary>
    /// Matches the labels to the finding IDs the last round left to label. Nothing is owed after round
    /// 1, after a round with no new finding, or in a run begun before the labels existed, and then
    /// whatever the call passed is ignored.
    /// </summary>
    /// <param name="round">The round whose findings are being labelled.</param>
    /// <param name="owed">The IDs of that round's new findings, from <see cref="RunState.FalloutOwed"/>.</param>
    /// <param name="labels">The labels the call passed, or null when it passed none.</param>
    /// <returns>How many findings carry each label, or null when nothing is owed.</returns>
    /// <exception cref="FalloutLabelsException">An owed ID has no label, a label names an ID that is not owed, or an ID has two labels.</exception>
    internal static FalloutTally? Check(int round, IReadOnlyList<string>? owed, PlanFallout? labels)
    {
        if (owed is not { Count: > 0 }) return null;

        var given = (labels?.Own ?? []).Concat(labels?.Sibling ?? [])
                                       .Concat(labels?.SameClass ?? [])
                                       .Concat(labels?.Older ?? [])
                                       .ToArray();
        var missing = owed.Except(given, StringComparer.Ordinal).ToArray();
        var unknown = given.Except(owed, StringComparer.Ordinal).ToArray();
        var repeated = given.GroupBy(id => id, StringComparer.Ordinal)
                            .Where(group => group.Count() > 1)
                            .Select(group => group.Key)
                            .ToArray();
        if (missing.Length + unknown.Length + repeated.Length == 0)
            return new FalloutTally(labels?.Own?.Count ?? 0, labels?.Sibling?.Count ?? 0,
                                    labels?.SameClass?.Count ?? 0, labels?.Older?.Count ?? 0);

        var problems = new[] { ("missing", missing), ("not among its new findings", unknown), ("under more than one label", repeated) }
            .Where(problem => problem.Item2.Length > 0)
            .Select(problem => $"{problem.Item1}: {string.Join(", ", problem.Item2)}");
        throw new FalloutLabelsException($"`fallout` does not match the {owed.Count} new findings of round {round} "
                                         + $"({string.Join(", ", owed)}); {string.Join("; ", problems)}. Pass each of those IDs "
                                         + "under exactly one of own, sibling, sameClass or older.");
    }

    /// <summary>The Flow-log line for the labels, which names only the labels that hold an ID.</summary>
    /// <param name="round">The round whose findings were labelled.</param>
    /// <returns>For example <c>Plan review round 3: own F-0021; sibling F-0024</c>.</returns>
    internal string Line(int round)
    {
        var labels = new[] { ("own", Own), ("sibling", Sibling), ("same-class", SameClass), ("older", Older) }
            .Where(label => label.Item2 is { Count: > 0 })
            .Select(label => $"{label.Item1} {string.Join(", ", label.Item2!)}");
        return $"Plan review round {round}: {string.Join("; ", labels)}";
    }

    /// <summary>The counts of the labels, for the tool-call record; the IDs stay out of it.</summary>
    /// <returns>For example <c>own 2, sibling 1, sameClass 0, older 1</c>.</returns>
    internal string Counts() =>
        $"own {Own?.Count ?? 0}, sibling {Sibling?.Count ?? 0}, sameClass {SameClass?.Count ?? 0}, older {Older?.Count ?? 0}";
}

/// <summary>How many of a plan-review round's new findings carry each label.</summary>
/// <param name="Own">The findings something an earlier revision wrote caused.</param>
/// <param name="Sibling">The findings a place left behind by an earlier revision caused.</param>
/// <param name="SameClass">The older instances of a class an earlier revision covered only in part.</param>
/// <param name="Older">The findings the review predates.</param>
internal sealed record FalloutTally(int Own, int Sibling, int SameClass, int Older)
{
    /// <summary>The round's new findings.</summary>
    internal int New => Own + Sibling + SameClass + Older;

    /// <summary>The new findings the plan's own revisions caused: <c>own</c> and <c>sibling</c>.</summary>
    internal int Fallout => Own + Sibling;

    /// <summary>Whether half the round or more is fallout, the point at which a further round waits for a sweep.</summary>
    internal bool Mostly => New > 0 && 2 * Fallout >= New;
}

/// <summary>The labels do not match the findings the last round left to label.</summary>
/// <param name="message">What is wrong with them and what to pass instead.</param>
internal sealed class FalloutLabelsException(string message) : Exception(message);
