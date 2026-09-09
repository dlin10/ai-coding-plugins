namespace UnknownEntryPointShapeFixture;

// A shape no recognizer in EntryPointTables.Default knows. Nothing in the indexer mentions IWorkUnit, so a
// handler of the kind a test row asks for can only have come from the row.

public static class Sink
{
    public static void Hit() { }
}

public interface IWorkUnit
{
    void Run();
}

public sealed class WorkUnit : IWorkUnit
{
    public void Run() => Sink.Hit();
}
