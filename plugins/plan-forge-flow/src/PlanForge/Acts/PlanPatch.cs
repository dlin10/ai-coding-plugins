using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PlanForge.Mcp;

namespace PlanForge.Acts;

internal sealed record PlanEdit(string? Op, string? Target, string? TargetHash, string? Content = null);

internal enum PlanValueKind { Hash, Id, Operation }

internal sealed record PlanPatchChange(string Op, string Target, string Label);

internal sealed record PlanPatchResult(string Plan, IReadOnlyList<PlanPatchChange> Changes, PlanPatchSizes Sizes);

internal sealed record PlanPatchSize(int Characters, int Utf8Bytes);

internal sealed record PlanPatchSizes(PlanPatchSize PayloadSize, PlanPatchSize Supplied,
                                      PlanPatchSize? PlanBefore = null, PlanPatchSize? Removed = null,
                                      PlanPatchSize? Written = null, PlanPatchSize? PlanAfter = null)
{
    public static PlanPatchSizes Payload(IReadOnlyList<PlanEdit?>? edits) =>
        new(Measure(JsonSerializer.Serialize(edits, (JsonTypeInfo<IReadOnlyList<PlanEdit?>?>)
            ToolArgumentJson.ArgumentOptions.GetTypeInfo(typeof(IReadOnlyList<PlanEdit?>)))),
            Measure(string.Concat(edits?.Select(edit => edit?.Content) ?? [])));

    internal PlanPatchSizes Before(string plan) => this with { PlanBefore = Measure(plan) };

    internal PlanPatchSizes Built(string removed, string written, string after) => this with
    {
        Removed = Measure(removed), Written = Measure(written), PlanAfter = Measure(after)
    };

    private static PlanPatchSize Measure(string text) => new(text.Length, Encoding.UTF8.GetByteCount(text));

    internal IEnumerable<(string Name, PlanPatchSize Size)> Measurements()
    {
        yield return ("payload", PayloadSize);
        yield return ("supplied", Supplied);
        if (PlanBefore is { } before) yield return ("planBefore", before);
        if (Removed is { } removed) yield return ("removed", removed);
        if (Written is { } written) yield return ("written", written);
        if (PlanAfter is { } after) yield return ("planAfter", after);
    }

    internal (string Name, string? Value)[] LogFields() =>
        Measurements().SelectMany(item => new (string, string?)[]
        {
            (item.Name + "Characters", item.Size.Characters.ToString(CultureInfo.InvariantCulture)),
            (item.Name + "Utf8Bytes", item.Size.Utf8Bytes.ToString(CultureInfo.InvariantCulture))
        }).ToArray();
}

internal sealed class PlanPatchRejectedException(string reason, PlanPatchSizes sizes, string message) : Exception(message)
{
    public string Reason { get; } = reason;
    public PlanPatchSizes Sizes { get; } = sizes;
}

internal static class PlanPatch
{
    public static PlanPatchResult Apply(string plan, string? baseHash, IReadOnlyList<PlanEdit?>? edits)
    {
        var sizes = PlanPatchSizes.Payload(edits).Before(plan);
        if (edits is null || edits.Count == 0)
            throw Reject("no_edits", "The patch must contain at least one edit.");

        var outline = PlanOutline.Of(plan);
        if (baseHash != outline.PlanHash)
            throw Reject("stale_base", $"The base hash {Shown(baseHash, PlanValueKind.Hash)} is not current. " +
                $"The current hash is {outline.PlanHash}; read forge.plan.outline and redo the edits against it.");

        var validated = new List<(PlanEdit Edit, int Target, int Index)>();
        for (var index = 0; index < edits.Count; index++)
        {
            var edit = edits[index];
            var name = $"Edit {index}, target {Shown(edit?.Target, PlanValueKind.Id)}";
            if (edit is null || edit.Op is null || edit.Target is null || edit.TargetHash is null)
                throw Reject("invalid_edit", $"{name}: the edit, operation, ID and target hash are required.");
            if (edit.Op is not ("replace" or "delete" or "insertBefore" or "insertAfter"))
                throw Reject("invalid_edit", $"{name}: unsupported operation {Shown(edit.Op, PlanValueKind.Operation)}.");
            var target = outline.Targets.ToList().FindIndex(target => target.Id == edit.Target);
            if (target < 0) throw Reject("unknown_target", $"{name}: the target is not in the current outline.");
            if (edit.TargetHash != outline.Targets[target].Hash)
                throw Reject("stale_target", $"{name}: target hash {Shown(edit.TargetHash, PlanValueKind.Hash)} " +
                    $"is not current ({outline.Targets[target].Hash}); read forge.plan.outline.");
            if (edit.Op == "delete" ? edit.Content is not null : string.IsNullOrWhiteSpace(edit.Content))
                throw Reject("invalid_edit", $"{name}: {Shown(edit.Op, PlanValueKind.Operation)} " +
                    (edit.Op == "delete" ? "must have no content." : "requires non-blank content."));
            validated.Add((edit, target, index));
        }

        var mutations = new Dictionary<int, PlanEdit>();
        var insertions = new Dictionary<int, PlanEdit>();
        foreach (var (edit, target, index) in validated)
        {
            var name = $"Edit {index}, target {Shown(edit.Target, PlanValueKind.Id)}";
            if (IsInsertion(edit))
            {
                var position = target + (edit.Op == "insertAfter" ? 1 : 0);
                if (!insertions.TryAdd(position, edit))
                    throw Reject("conflicting_edits", $"{name}: two insertions occupy the same position.");
            }
            else if (!mutations.TryAdd(target, edit))
                throw Reject("conflicting_edits", $"{name}: two edits replace or delete the same target.");
        }
        foreach (var (edit, target, index) in validated)
            if (IsInsertion(edit) && mutations.TryGetValue(target, out var mutation) && mutation.Op == "delete")
                throw Reject("conflicting_edits", $"Edit {index}, target {Shown(edit.Target, PlanValueKind.Id)}: " +
                    "an insertion is anchored on a deleted target.");

        var newline = plan.Contains('\n') && plan.Replace("\r\n", "", StringComparison.Ordinal).IndexOfAny(['\r', '\n']) < 0
            ? "\r\n" : "\n";
        var pieces = new List<string>();
        var removed = new StringBuilder();
        var written = new StringBuilder();
        for (var index = 0; index <= outline.Targets.Count; index++)
        {
            if (insertions.TryGetValue(index, out var insertion)) AddWritten(insertion.Content!);
            if (index == outline.Targets.Count) break;
            var target = outline.Targets[index];
            var original = plan.Substring(target.Start, target.Length);
            if (!mutations.TryGetValue(index, out var mutation)) pieces.Add(original);
            else
            {
                removed.Append(original);
                if (mutation.Op == "replace") AddWritten(mutation.Content!);
            }
        }

        var result = new StringBuilder();
        foreach (var piece in pieces)
        {
            if (result.Length > 0 && result[^1] is not ('\r' or '\n')) result.Append(newline);
            result.Append(piece);
        }
        var after = result.ToString().TrimStart('\uFEFF');
        sizes = sizes.Built(removed.ToString(), written.ToString(), after);
        if (PlanOutline.IsBlank(after)) throw Reject("blank_result", "The patch would leave a blank plan.");
        if (after == plan) throw Reject("no_change", "The patch would leave the plan unchanged.");
        if (Parses(plan, out _) && !Parses(after, out var failure))
            throw Reject("shape_broken", $"The patch would break the task shape: {failure}.");

        var changes = validated.Select(item => new PlanPatchChange(item.Edit.Op!, item.Edit.Target!,
            outline.Targets[item.Target].Label)).ToArray();
        return new PlanPatchResult(after, changes, sizes);

        PlanPatchRejectedException Reject(string reason, string message) => new(reason, sizes, message);

        void AddWritten(string text)
        {
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                                 .Replace("\n", newline, StringComparison.Ordinal);
            if (!normalized.EndsWith(newline, StringComparison.Ordinal)) normalized += newline;
            // Leading BOMs removed from the result are absent from the written text as it lands.
            written.Append(pieces.Count == 0 ? normalized.TrimStart('\uFEFF') : normalized);
            pieces.Add(normalized);
        }
    }

    public static string Shown(string? value, PlanValueKind kind)
    {
        var name = kind switch { PlanValueKind.Hash => "hash", PlanValueKind.Id => "ID", _ => "operation" };
        if (value is null) return $"a missing {name}";
        var valid = kind switch
        {
            PlanValueKind.Hash => value.Length == 16 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            PlanValueKind.Id => value.Length > 1 && value[0] == 's' && value[1..].All(character => character is >= '0' and <= '9'),
            _ => value is "replace" or "delete" or "insertBefore" or "insertAfter"
        };
        return valid ? value : $"a malformed {name} of {value.Length} characters";
    }

    private static bool IsInsertion(PlanEdit edit) => edit.Op is "insertBefore" or "insertAfter";

    private static bool Parses(string plan, out string? failure)
    {
        try
        {
            PlanTasks.Parse(plan);
            failure = null;
            return true;
        }
        catch (PlanShapeException error)
        {
            failure = error.Message switch
            {
                "the plan must contain exactly one '## Approach' section" => "the Approach heading count",
                "the plan's Approach section has no numbered tasks" => "no tasks",
                "plan tasks must be numbered 1..N, in order" => "misnumbered tasks",
                _ => "invalid task shape"
            };
        }
        catch (FormatException) { failure = "an unreadable task number"; }
        catch (OverflowException) { failure = "an overflowing task number"; }
        return false;
    }
}
