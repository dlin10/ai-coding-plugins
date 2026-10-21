namespace PlanForge.Tests;

/// <summary>
/// The forge skill as an Orchestrator reads it: <c>SKILL.md</c>, then every file under its
/// <c>references/</c> in name order. Most sections live there, so the core stays under the host's
/// re-injection budget after a compaction (docs/adr/0040); a pinned phrase may be in either.
/// </summary>
internal static class ForgeSkill
{
    /// <summary>The core and every reference file, joined in that order.</summary>
    /// <param name="pluginRoot">The plugin's root folder, the one holding <c>skills/</c>.</param>
    public static string Text(string pluginRoot)
    {
        var skill = Path.Combine(pluginRoot, "skills", "forge");
        var references = Directory.EnumerateFiles(Path.Combine(skill, "references"), "*.md").Order(StringComparer.Ordinal);
        return string.Join("\n\n", references.Prepend(Path.Combine(skill, "SKILL.md")).Select(File.ReadAllText));
    }
}
