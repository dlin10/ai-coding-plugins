namespace CodexSessionPulse.JsonlInspect;

/// <summary>
/// Read-only dump of the per-request token usage in Codex rollout files: one line per
/// <c>token_count</c> event with its timestamp, thread, model, token counts and whether the turn
/// reported timing. It never prints message text or any other payload.
/// </summary>
internal static class Program
{
    private const string Usage = "usage: jsonl-inspect <rollout.jsonl> [<rollout.jsonl> ...]";

    /// <summary>Prints the usage rows of every rollout named on the command line.</summary>
    /// <param name="args">Paths of plain <c>.jsonl</c> rollout files.</param>
    /// <returns>0 when every file was read, 1 when one was missing or compressed, 2 on bad usage.</returns>
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var exitCode = 0;
        foreach (var path in args)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"not found: {path}");
                exitCode = 1;
                continue;
            }

            if (path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"compressed segment, decompress it first: {path}");
                exitCode = 1;
                continue;
            }

            Console.WriteLine($"# {Path.GetFileName(path)}");
            var result = RolloutReader.Read(path);
            foreach (var row in result.Rows)
                Console.WriteLine(row.Format());
            if (result.UnparsedLines > 0)
                Console.WriteLine($"# {result.UnparsedLines} line(s) were not valid JSON and were skipped");
        }

        return exitCode;
    }
}
