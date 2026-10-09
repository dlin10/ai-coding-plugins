using PlanForge.Acts;
using PlanForge.Run;
using Xunit;

namespace PlanForge.Tests;

public sealed partial class PlanGatesTests
{
    public static IEnumerable<object[]> FixGrammarCases()
    {
        const string heading = "## Gates\n1. **G1.** full condition\n";
        foreach (var label in new[] { "**Fix gate:**", "**Fix gate**:", "**Fix gate**", "**fIX GaTe:**", "2. **Fix gate:**", "  2. **Fix gate**:" })
            yield return [heading + label + " `cmd` (R2)", "cmd"];
        foreach (var g in new[] { "1. **G1**", "1. **G1.**", "1. **G1:**", "1. **G1**:", "1. **G12**.", "  3. **G3.**" })
            yield return ["## Gates\n" + g + " condition\n**Fix gate:** `cmd`", "cmd"];
        yield return [heading + "**Fix gate:** `cmd`\n2. **G2.** `full2`", "cmd"];
        yield return [heading + "### Details\n**Fix gate:** `cmd`", "cmd"];
        yield return [heading + "**Fix gate:** `cmd`\n## Gates\n**Fix gate:** `ignored`", "cmd"];
        yield return [heading + "**Fix gate:** `cmd`\n# End\n**Fix gate:** `ignored`", "cmd"];
        yield return [heading + "**Fix gate:** `cmd`\n## End\n**Fix gate:** `ignored`", "cmd"];
        yield return ["```markdown\n## Gates\n**Fix gate:** `fake`\n```\n" + heading + "**Fix gate:** `cmd`", "cmd"];
        yield return ["~~~~markdown\n## Gates\n~~~\n**Fix gate:** `fake`\n~~~~~\n" + heading + "**Fix gate:** `cmd`", "cmd"];
        yield return [heading + "````markdown\n```\n**Fix gate:** `fake`\n1. **G9** example\n```\n`````\n**Fix gate:** `cmd`", "cmd"];
        yield return [heading + "mention **Fix gate:** `fake`\n- **Fix gate:** `fake`\n**Fix gate:** `cmd`", "cmd"];
        foreach (var fence in new[] { "```", "````", "~~~", "~~~~~" })
        {
            yield return [heading + "**Fix gate:**\n" + fence + "powershell\n  cmd  \n\n  next  \n" + fence, "cmd\nnext"];
            yield return [heading + "**Fix gate:** " + fence + "powershell\ncmd\n" + fence + fence[0], "cmd"];
        }
        yield return [heading + "**Fix gate:**\n````ps\ncmd\n```\n nested \n```\nnext\n````", "cmd\n```\nnested\n```\nnext"];
        yield return [heading + "**Fix gate:**\n~~~~\ncmd\n~~~\n**Fix gate:** nested command text\n~~~~", "cmd\n~~~\n**Fix gate:** nested command text"];
    }

    [Theory]
    [MemberData(nameof(FixGrammarCases))]
    public void Fix_gate_grammar_accepts_real_labels_and_preserves_commands(string plan, string expected)
    {
        foreach (var newline in new[] { "\n", "\r\n" })
        {
            var gate = PlanGates.FixGate(plan.Replace("\n", newline, StringComparison.Ordinal));
            Assert.Equal("Fix gate", gate.Label);
            Assert.Equal(expected, gate.Command);
        }
    }

    public static IEnumerable<object[]> InvalidFixGrammarCases()
    {
        const string heading = "## Gates\n1. **G1.** condition\n";
        foreach (var code in new[] { "", "prose `cmd`", "` `", "``", "```ps\n\n```", "~~~\n  \n~~~", "```ps\ncmd", "~~~~\ncmd\n~~~", "```\ncmd\n~~~", "```\ncmd\n``` suffix", "`cmd" })
            yield return [heading + "**Fix gate:** " + code, "executable Fix gate"];
        foreach (var label in new[] { "- **Fix gate:**", "mention **Fix gate:**", "2. mention **Fix gate:**", "**Fix gatekeeper:**" })
            yield return [heading + label + " `cmd`", "none was found"];
        foreach (var g in new[] { "**G1.** condition", "1. mention **G1.** condition", "mention 1. **G1.**", "- **G1.** condition", "1. **g1** condition", "1. **G1suffix** condition", "```md\n1. **G1.** example\n```" })
            yield return ["## Gates\n" + g + "\n**Fix gate:** `cmd`", "numbered **G<n>**"];
        yield return [heading + "**Fix gate:** `cmd`\n**Fix gate:** prose", "duplicate"];
        yield return [heading + "**Fix gate:** prose\n2. **Fix gate:** `cmd`", "duplicate"];
        yield return [heading + "**Fix gate:**\n**Fix gate:**", "duplicate"];
        yield return [heading + "## Gates\n**Fix gate:** `cmd`", "none was found"];
        yield return [heading + "# End\n**Fix gate:** `cmd`", "none was found"];
        yield return [heading + "## End\n**Fix gate:** `cmd`", "none was found"];
        yield return ["## gates\n1. **G1** condition\n**Fix gate:** `cmd`", "none was found"];
        yield return ["## Gates extra\n1. **G1** condition\n**Fix gate:** `cmd`", "none was found"];
        yield return ["### Gates\n1. **G1** condition\n**Fix gate:** `cmd`", "none was found"];
        yield return ["````md\n```\n" + heading + "**Fix gate:** `cmd`\n```", "none was found"];
        yield return [heading + "```md\n**Fix gate:** `cmd`", "none was found"];
    }

    [Theory]
    [MemberData(nameof(InvalidFixGrammarCases))]
    public void Fix_gate_grammar_refuses_fake_labels_missing_entries_and_non_commands(string plan, string reason)
    {
        foreach (var newline in new[] { "\n", "\r\n" })
        {
            var error = Assert.Throws<ArgumentRejectedException>(() => PlanGates.FixGate(plan.Replace("\n", newline, StringComparison.Ordinal)));
            Assert.Contains(reason, error.Message);
        }
    }
}
