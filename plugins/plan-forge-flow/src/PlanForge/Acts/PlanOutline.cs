using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PlanForge.Acts;

internal sealed record PlanTarget(string Id, string Kind, string Label, string Hash, int Characters,
                                  [property: JsonIgnore] int Start, [property: JsonIgnore] int Length);

internal sealed partial record PlanOutline(string PlanHash, int Characters, IReadOnlyList<PlanTarget> Targets)
{
    public static PlanOutline Of(string plan)
    {
        var layout = PlanTasks.Locate(plan);
        var starts = new SortedDictionary<int, string>();
        foreach (Match heading in HeadingLine().Matches(plan))
            if (layout is null || heading.Index < layout.Start || heading.Index >= layout.End)
                starts[heading.Index] = "heading";

        if (layout is not null)
        {
            starts[layout.Start] = "heading";
            foreach (var taskStart in layout.TaskStarts) starts[taskStart] = "task";
            if (layout.End < plan.Length) starts[layout.End] = "heading";
        }
        if (plan.Length > 0 && !starts.ContainsKey(0)) starts[0] = "preamble";

        var boundaries = starts.ToArray();
        var targets = new List<PlanTarget>(boundaries.Length);
        for (var index = 0; index < boundaries.Length; index++)
        {
            var (start, kind) = boundaries[index];
            var end = index + 1 < boundaries.Length ? boundaries[index + 1].Key : plan.Length;
            var text = plan[start..end];
            var lines = text.Split('\n');
            var label = kind == "preamble"
                ? lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "(blank)"
                : lines[0].Trim();
            if (label.Length > 80) label = label[..80];
            targets.Add(new PlanTarget($"s{index}", kind, label, Hash(text), text.Length, start, end - start));
        }

        return new PlanOutline(Hash(plan), plan.Length, targets);
    }

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];

    public static bool IsBlank(string? text) =>
        text is null || string.IsNullOrWhiteSpace(text.TrimStart('\uFEFF'));

    [GeneratedRegex("^#{1,6}[ \\t]", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingLine();
}
