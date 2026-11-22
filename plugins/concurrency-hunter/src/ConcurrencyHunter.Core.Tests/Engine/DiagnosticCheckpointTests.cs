using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class DiagnosticCheckpointTests
{
    [Fact]
    public void Checkpoint_reports_memory_and_details_then_pauses_until_go()
    {
        var folder = Directory.CreateTempSubdirectory("ch-diag-").FullName;
        try
        {
            // The go file is already there, so the pause writes its ready file and goes on at once.
            File.WriteAllText(Path.Combine(folder, "entries_Scope.go"), "go");
            var asked = false;
            DiagnosticCheckpoint.ReachIn(null, ["entries"], "entries:Off", collect: false, () => { asked = true; return ["never"]; });
            DiagnosticCheckpoint.ReachIn(folder, ["entries"], "entries:Scope", collect: false, () => ["pairs=3", "regions=2"]);

            Assert.False(asked);
            var lines = File.ReadAllLines(Path.Combine(folder, "checkpoints.log")).Select(line => line[(line.IndexOf(' ') + 1)..]).ToArray();
            Assert.Equal(5, lines.Length);
            Assert.StartsWith("entries:Scope collected=False collectMs=", lines[0]);
            Assert.Contains(" totalMemory=", lines[0]);
            Assert.Equal(["entries:Scope pairs=3", "entries:Scope regions=2", "entries:Scope paused", "entries:Scope resumed"], lines[1..]);
            Assert.True(File.Exists(Path.Combine(folder, "entries_Scope.ready")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Checkpoints_reached_on_several_threads_at_once_write_every_line_whole()
    {
        // Access collection on several workers (ADR 0040) must not make a report line fail or break another one.
        var folder = Directory.CreateTempSubdirectory("ch-diag-").FullName;
        try
        {
            const int THREADS = 8;
            const int CHECKPOINTS = 25;
            Parallel.For(0, THREADS, new ParallelOptions { MaxDegreeOfParallelism = THREADS }, thread =>
            {
                for (var at = 0; at < CHECKPOINTS; at++)
                    DiagnosticCheckpoint.ReachIn(folder, [], $"execution:T{thread}", collect: false, () => [$"n={at}"]);
            });

            var lines = File.ReadAllLines(Path.Combine(folder, "checkpoints.log")).Select(line => line[(line.IndexOf(' ') + 1)..]).ToArray();
            Assert.Equal(THREADS * CHECKPOINTS * 2, lines.Length);
            for (var thread = 0; thread < THREADS; thread++)
            {
                var name = $"execution:T{thread} ";
                Assert.Equal(CHECKPOINTS, lines.Count(line => line.StartsWith(name + "collected=False collectMs=", StringComparison.Ordinal) &&
                                                             line.Contains(" workingSet=", StringComparison.Ordinal)));
                Assert.Equal(Enumerable.Range(0, CHECKPOINTS).Select(at => $"{name}n={at}"),
                             lines.Where(line => line.StartsWith(name + "n=", StringComparison.Ordinal)));
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
