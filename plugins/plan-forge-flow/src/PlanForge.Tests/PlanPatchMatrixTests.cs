using System.Text;
using PlanForge.Acts;
using Xunit;
using static PlanForge.Tests.PlanPatchTests;

namespace PlanForge.Tests;

public sealed class PlanPatchMatrixTests
{
    private static readonly string[] Operations = ["replace", "delete", "insertBefore", "insertAfter"];
    private static readonly string[] Contents = ["null", "empty", "whitespace", "nonblank"];
    private static readonly string[] Styles = ["LF", "CRLF", "mixed", "none"];
    private static readonly string[] TextStyles = ["LF", "CRLF", "mixed", "multiline", "single", "two-breaks"];
    private static readonly string[] EditSets = ["middle", "after-final", "replace-final-and-after"];
    private static readonly string[] BaseKinds = ["parses", "shape", "non-ASCII", "overflow"];
    private static readonly string[] ResultKinds = ["parses", "shape", "non-ASCII", "overflow", "blank", "equal", "equal-after-BOM", "BOM-only"];
    private const string FiveTargets = "# A\nA\n# B\nB\n# C\nC\n# D\nD\n# E\nE\n";

    private sealed record Input(string Op, int Target, string? Content);
    private sealed record PairCell(string First, string Second, int Distance);
    private sealed record ContentCell(string Op, string Content);
    private sealed record BreakCell(string Style, string Text, string Edits);
    private sealed record ValidationCell(string Base, string Result);

    private static IEnumerable<PairCell> Pairs =>
        from first in Operations from second in Operations from distance in new[] { 0, 1, 2 }
        select new PairCell(first, second, distance);

    private static IEnumerable<ContentCell> ContentCells =>
        from op in Operations.Append("unknown") from content in Contents select new ContentCell(op, content);

    private static IEnumerable<BreakCell> BreakCells =>
        from style in Styles from text in TextStyles from edits in EditSets select new BreakCell(style, text, edits);

    private static IEnumerable<ValidationCell> ValidationCells =>
        from basis in BaseKinds from result in ResultKinds select new ValidationCell(basis, result);

    [Fact]
    public void Every_edit_pair_is_decided_as_expected()
    {
        var cells = Pairs.ToArray();
        Assert.Equal(48, cells.Length);
        foreach (var cell in cells)
        {
            var inputs = PairInputs(cell);
            var edits = Wire(FiveTargets, inputs);
            if (Conflicts(cell)) Reject(FiveTargets, "conflicting_edits", edits);
            else Assert.Equal(Reference(FiveTargets, inputs), Apply(FiveTargets, edits).Plan);
        }
    }

    [Fact]
    public void Every_pair_is_decided_the_same_in_either_order()
    {
        foreach (var cell in Pairs)
        {
            var edits = Wire(FiveTargets, PairInputs(cell));
            var reverse = edits.Reverse().ToArray();
            if (Conflicts(cell))
                Assert.Equal(Reject(FiveTargets, "conflicting_edits", edits).Reason,
                    Reject(FiveTargets, "conflicting_edits", reverse).Reason);
            else Assert.Equal(Apply(FiveTargets, edits).Plan, Apply(FiveTargets, reverse).Plan);
        }
    }

    [Fact]
    public void Every_edit_content_cell_is_decided_as_expected()
    {
        var cells = ContentCells.ToArray();
        Assert.Equal(20, cells.Length);
        foreach (var cell in cells)
        {
            var content = cell.Content switch { "null" => null, "empty" => "", "whitespace" => " \t\n", _ => "New text" };
            var target = PlanOutline.Of(FiveTargets).Targets[1];
            var edit = new PlanEdit(cell.Op, target.Id, target.Hash, content);
            var accepted = cell.Op == "delete" ? content is null :
                Operations.Contains(cell.Op) && cell.Content == "nonblank";
            if (accepted)
                Assert.Equal(Reference(FiveTargets, [new Input(cell.Op, 1, content)]), Apply(FiveTargets, edit).Plan);
            else Reject(FiveTargets, "invalid_edit", edit);
        }
    }

    [Fact]
    public void Every_line_break_cell_builds_the_expected_result()
    {
        var cells = BreakCells.ToArray();
        Assert.Equal(72, cells.Length);
        foreach (var cell in cells)
        {
            var plan = cell.Style switch
            {
                "LF" => "# A\nA\n# B\nB\n# C\nC",
                "CRLF" => "# A\r\nA\r\n# B\r\nB\r\n# C\r\nC",
                "mixed" => "# A\r\nA\n# B\nB\r\n# C\nC",
                _ => "A"
            };
            var text = cell.Text switch
            {
                "LF" => "Written\ntext\n",
                "CRLF" => "Written\r\ntext\r\n",
                "mixed" => "Written\r\ntext\nend\r\n",
                "multiline" => "Written\ntext",
                "single" => "Written",
                _ => "Written\ntext\n\n"
            };
            var final = cell.Style == "none" ? 0 : 2;
            Input[] inputs = cell.Edits switch
            {
                "middle" => [new("replace", final == 0 ? 0 : 1, text)],
                "after-final" => [new("insertAfter", final, text)],
                _ => [new("replace", final, text), new("insertAfter", final, text)]
            };
            var actual = Apply(plan, Wire(plan, inputs)).Plan;

            Assert.Equal(Reference(plan, inputs, cell.Style == "CRLF" ? "\r\n" : "\n"), actual);
        }
    }

    [Fact]
    public void Every_validation_cell_is_decided_as_expected()
    {
        var cells = ValidationCells.ToArray();
        Assert.Equal(32, cells.Length);
        foreach (var cell in cells)
        {
            var plan = Shape(cell.Base);
            var changed = Shape(cell.Result).Replace("# Brief", "# Updated brief", StringComparison.Ordinal);
            var targets = PlanOutline.Of(plan).Targets;
            PlanEdit?[] edits;
            if (cell.Result == "blank") edits = targets.Select((_, index) => (PlanEdit?)Edit(plan, index, "delete")).ToArray();
            else if (cell.Result is "equal" or "equal-after-BOM")
                edits = [Edit(plan, 0, "replace", (cell.Result == "equal-after-BOM" ? "\uFEFF\uFEFF" : "") +
                    plan.Substring(targets[0].Start, targets[0].Length))];
            else
                edits = targets.Select((_, index) => (PlanEdit?)(index == 0
                    ? Edit(plan, index, "replace", cell.Result == "BOM-only" ? "\uFEFF\uFEFF" : changed)
                    : Edit(plan, index, "delete"))).ToArray();

            var reason = cell.Result switch
            {
                "blank" or "BOM-only" => "blank_result",
                "equal" or "equal-after-BOM" => "no_change",
                "shape" or "non-ASCII" or "overflow" when cell.Base == "parses" => "shape_broken",
                _ => null
            };
            if (reason is not null) Reject(plan, reason, edits);
            else Assert.Equal(changed, Apply(plan, edits).Plan);
        }
    }

    [Fact]
    public void Every_axis_value_occurs_in_some_cell()
    {
        Assert.Equal(Operations, Pairs.Select(cell => cell.First).Distinct());
        Assert.Equal(Operations, Pairs.Select(cell => cell.Second).Distinct());
        Assert.Equal(new[] { 0, 1, 2 }, Pairs.Select(cell => cell.Distance).Distinct());
        Assert.Equal(Operations.Append("unknown"), ContentCells.Select(cell => cell.Op).Distinct());
        Assert.Equal(Contents, ContentCells.Select(cell => cell.Content).Distinct());
        Assert.Equal(Styles, BreakCells.Select(cell => cell.Style).Distinct());
        Assert.Equal(TextStyles, BreakCells.Select(cell => cell.Text).Distinct());
        Assert.Equal(EditSets, BreakCells.Select(cell => cell.Edits).Distinct());
        Assert.Equal(BaseKinds, ValidationCells.Select(cell => cell.Base).Distinct());
        Assert.Equal(ResultKinds, ValidationCells.Select(cell => cell.Result).Distinct());
    }

    private static Input[] PairInputs(PairCell cell) =>
        [new(cell.First, 1, cell.First == "delete" ? null : "First written"),
         new(cell.Second, 1 + cell.Distance, cell.Second == "delete" ? null : "Second written")];

    private static bool Conflicts(PairCell cell)
    {
        var firstInserts = cell.First is "insertBefore" or "insertAfter";
        var secondInserts = cell.Second is "insertBefore" or "insertAfter";
        if (cell.Distance == 0 && !firstInserts && !secondInserts) return true;
        if (cell.Distance == 0 && (cell.First == "delete" && secondInserts || cell.Second == "delete" && firstInserts)) return true;
        if (!firstInserts || !secondInserts) return false;
        return cell.Distance == 0 && cell.First == cell.Second ||
               cell.Distance == 1 && cell.First == "insertAfter" && cell.Second == "insertBefore";
    }

    private static PlanEdit?[] Wire(string plan, Input[] inputs) =>
        inputs.Select(input => (PlanEdit?)Edit(plan, input.Target, input.Op, input.Content)).ToArray();

    private static string Shape(string kind) => kind switch
    {
        "parses" => "# Brief\n## Approach\n1. First\n2. Second\n## Gates\nCheck\n",
        "shape" => "# Brief\nThere is no Approach.\n",
        "non-ASCII" => "# Brief\n## Approach\n" + new string('\u0661', 90) + ". Private\n",
        _ => "# Brief\n## Approach\n" + new string('9', 90) + ". Private\n"
    };

    // A literal R4 reference: collect pieces from the target list, then join their boundaries.
    // It does not call the patch engine or its normalization or validation helpers.
    private static string Reference(string plan, Input[] edits, string newline = "\n")
    {
        var targets = PlanOutline.Of(plan).Targets;
        var pieces = new List<string>();
        for (var position = 0; position <= targets.Count; position++)
        {
            foreach (var edit in edits)
                if (edit.Op == "insertBefore" && edit.Target == position || edit.Op == "insertAfter" && edit.Target + 1 == position)
                    pieces.Add(ReferenceWritten(edit.Content!, newline));
            if (position == targets.Count) continue;
            var mutation = edits.SingleOrDefault(edit => edit.Target == position && edit.Op is "replace" or "delete");
            if (mutation is null)
                pieces.Add(plan.Substring(targets[position].Start, targets[position].Length));
            else if (mutation.Op == "replace") pieces.Add(ReferenceWritten(mutation.Content!, newline));
        }
        var output = new StringBuilder();
        for (var index = 0; index < pieces.Count; index++)
        {
            output.Append(pieces[index]);
            if (index + 1 < pieces.Count && !pieces[index].EndsWith('\n') && !pieces[index].EndsWith('\r')) output.Append(newline);
        }
        return output.ToString().TrimStart('\uFEFF');
    }

    private static string ReferenceWritten(string text, string newline)
    {
        var result = new StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                result.Append(newline);
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
            }
            else if (text[index] == '\n') result.Append(newline);
            else result.Append(text[index]);
        }
        if (text[^1] is not ('\r' or '\n')) result.Append(newline);
        return result.ToString();
    }
}
