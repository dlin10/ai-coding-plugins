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
}
