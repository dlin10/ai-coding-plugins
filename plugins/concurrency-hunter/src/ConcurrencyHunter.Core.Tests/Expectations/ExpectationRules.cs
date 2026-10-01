namespace ConcurrencyHunter.Core.Tests.Expectations;

internal static class ExpectationRules
{
    internal static IReadOnlyList<string> Violations(ExpectationFile file)
    {
        var entries = Entries(file).ToArray();
        var violations = new List<string>();
        foreach (var entry in entries.Where(entry => entry.Until is not null))
        {
            var knownUntil = true;
            try
            {
                _ = PhaseOrder.Compare(entry.Until!, entry.Until!);
            }
            catch (ArgumentOutOfRangeException)
            {
                knownUntil = false;
                violations.Add($"{entry.Id}: until '{entry.Until}' must name a known phase.");
            }

            if (knownUntil && PhaseOrder.Compare(entry.Until!, entry.Phase) <= 0)
                violations.Add($"{entry.Id}: until '{entry.Until}' must come after phase '{entry.Phase}'.");
            var caseId = entry.Id.Split('/')[0];
            if (!entries.Any(replacement => replacement.Id.Split('/')[0] == caseId && replacement.Phase == entry.Until))
                violations.Add($"{entry.Id}: until '{entry.Until}' needs a replacement of case '{caseId}' at that phase.");
        }
        return violations;
    }

    internal static IReadOnlyList<string> Changes(ExpectationFile head, ExpectationFile current)
    {
        var entries = Entries(current).ToArray();
        return Entries(head)
            .Where(entry => entry.Until is not null && entries.FirstOrDefault(candidate => candidate.Id == entry.Id).Until != entry.Until)
            .Select(entry => $"{entry.Id}: written until '{entry.Until}' changed or was removed.")
            .ToArray();
    }

    private static IEnumerable<(string Id, string Phase, string? Until)> Entries(ExpectationFile file) =>
        file.Findings.Select(entry => (entry.Id, entry.Phase, entry.Until))
            .Concat(file.NotDefects.Select(entry => (entry.Id, entry.Phase, entry.Until)));
}
