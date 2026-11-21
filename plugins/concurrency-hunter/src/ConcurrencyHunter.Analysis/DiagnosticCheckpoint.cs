using System.Diagnostics;

namespace ConcurrencyHunter;

/// <summary>Points a diagnostic run reports what the process holds at, as the stage reaches them rather than at the end of the run:
/// off, and costing nothing, unless the environment variable <c>CH_DIAG_DIR</c> names a folder. A report goes to
/// <c>checkpoints.log</c> in that folder and to standard error; a checkpoint whose name contains an entry of the comma-separated
/// <c>CH_DIAG_PAUSE</c> then writes <c>&lt;name&gt;.ready</c> and waits for <c>&lt;name&gt;.go</c>, so that a dump can be taken there.
/// The analysis is the same either way.</summary>
internal static class DiagnosticCheckpoint
{
    private static readonly string? FOLDER = Environment.GetEnvironmentVariable("CH_DIAG_DIR");
    private static readonly string[] PAUSES = (Environment.GetEnvironmentVariable("CH_DIAG_PAUSE") ?? string.Empty)
                                              .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Reports the process's memory at a checkpoint, then the details, each line as soon as it is known, and pauses there when
    /// asked to.</summary>
    /// <param name="name">The checkpoint, with the scope it is reached in.</param>
    /// <param name="collect">Whether to run a full blocking compacting collection first, so that the heap size is what is live.</param>
    /// <param name="details">The lines to report after the memory, computed only in a diagnostic run.</param>
    internal static void Reach(string name, bool collect, Func<IEnumerable<string>>? details = null) =>
        ReachIn(FOLDER, PAUSES, name, collect, details);

    /// <summary>Reports at a checkpoint into the given folder, pausing there when a pause entry is part of its name; nothing happens
    /// without a folder. The environment-driven <see cref="Reach"/> calls it, and so does a test, since the environment is the whole
    /// process's.</summary>
    /// <param name="folder">The folder the report and the pause files go to, or null when diagnostics are off.</param>
    /// <param name="pauses">The name parts that make a checkpoint pause.</param>
    /// <param name="name">The checkpoint, with the scope it is reached in.</param>
    /// <param name="collect">Whether to run a full blocking compacting collection first, so that the heap size is what is live.</param>
    /// <param name="details">The lines to report after the memory, computed only when there is a folder.</param>
    internal static void ReachIn(string? folder, IReadOnlyCollection<string> pauses, string name, bool collect,
                                 Func<IEnumerable<string>>? details)
    {
        if (folder is null)
            return;
        var clock = Stopwatch.StartNew();
        if (collect)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        using var process = Process.GetCurrentProcess();
        Report(folder, $"{name} collected={collect} collectMs={clock.ElapsedMilliseconds} heapSize={info.HeapSizeBytes} " +
                       $"fragmented={info.FragmentedBytes} gcCommitted={info.TotalCommittedBytes} totalMemory={GC.GetTotalMemory(false)} " +
                       $"private={process.PrivateMemorySize64} workingSet={process.WorkingSet64}");
        if (details is not null)
            foreach (var line in details())
            {
                WorkCancellation.Check();
                Report(folder, $"{name} {line}");
            }
        if (!pauses.Any(pause => name.Contains(pause, StringComparison.Ordinal)))
            return;
        var ready = Path.Combine(folder, $"{Safe(name)}.ready");
        var go = Path.Combine(folder, $"{Safe(name)}.go");
        File.WriteAllText(ready, Environment.ProcessId.ToString());
        Report(folder, $"{name} paused");
        while (!File.Exists(go))
        {
            WorkCancellation.Check();
            Thread.Sleep(1000);
        }
        Report(folder, $"{name} resumed");
    }

    /// <summary>Writes a report line, with the time, to the folder's log and to standard error.</summary>
    /// <param name="folder">The folder that holds <c>checkpoints.log</c>.</param>
    /// <param name="line">The line.</param>
    private static void Report(string folder, string line)
    {
        var stamped = $"{DateTimeOffset.Now:o} {line}";
        File.AppendAllText(Path.Combine(folder, "checkpoints.log"), stamped + Environment.NewLine);
        Console.Error.WriteLine(stamped);
    }

    /// <summary>A checkpoint name as a file name.</summary>
    /// <param name="name">The checkpoint.</param>
    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
}
