namespace ConcurrencyHunter.Core.Tests.Engine;

internal static class ProbeRevision
{
    internal static string Read(string root)
    {
        var directory = Path.Combine(root, ".git");
        if (File.Exists(directory))
            directory = Path.GetFullPath(File.ReadAllText(directory).Trim()["gitdir: ".Length..], root);
        var head = File.ReadAllText(Path.Combine(directory, "HEAD")).Trim();
        if (!head.StartsWith("ref: ", StringComparison.Ordinal))
            return head;
        var reference = head["ref: ".Length..];
        if (File.Exists(Path.Combine(directory, "commondir")))
            directory = Path.GetFullPath(File.ReadAllText(Path.Combine(directory, "commondir")).Trim(), directory);
        var loose = Path.Combine(directory, reference);
        return File.Exists(loose) ? File.ReadAllText(loose).Trim() :
            File.ReadLines(Path.Combine(directory, "packed-refs")).Single(line => line.EndsWith(" " + reference, StringComparison.Ordinal)).Split(' ')[0];
    }
}
