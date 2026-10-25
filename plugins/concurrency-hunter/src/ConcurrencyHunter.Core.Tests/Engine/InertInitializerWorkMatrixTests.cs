using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class InertInitializerWorkMatrixTests
{
    private enum Work { TaskRun, AsyncCall, Timer }
    private enum Site { Initializer, Helper }
    private sealed record Cell(Work Work, Site Site, TypeInitializerTreatment Treatment);

    private static IEnumerable<Cell> Cells() =>
        from work in Enum.GetValues<Work>()
        from site in Enum.GetValues<Site>()
        from treatment in Enum.GetValues<TypeInitializerTreatment>()
        select new Cell(work, site, treatment);

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        var cells = Cells().ToArray();
        Assert.Equal(Enum.GetValues<Work>(), cells.Select(cell => cell.Work).Distinct().Order());
        Assert.Equal(Enum.GetValues<Site>(), cells.Select(cell => cell.Site).Distinct().Order());
        Assert.Equal(Enum.GetValues<TypeInitializerTreatment>(), cells.Select(cell => cell.Treatment).Distinct().Order());
    }

    [Fact]
    public void Every_cell_gets_its_expected_treatment() => Check(work: false);

    [Fact]
    public void Every_cell_starts_its_work_as_expected() => Check(work: true);

    private static void Check(bool work)
    {
        foreach (var cell in Cells())
        {
            var start = cell.Work switch
            {
                Work.TaskRun => "_ = Task.Run(() => Other.Count++);",
                Work.AsyncCall => "_ = Later();",
                Work.Timer => "_ = new Timer(_ => Other.Count++, null, 0, 100);",
                _ => throw new ArgumentOutOfRangeException()
            };
            var source = $$"""
                using System.Threading;
                using System.Threading.Tasks;
                public static class Other { public static int Count; }
                public class Registry
                {
                    public static object Value;
                    static Registry() { Value = new object(); {{(cell.Site == Site.Initializer ? start : "Start();")}} }
                    private static void Start() { {{start}} }
                    private static async Task Later() { await Task.Yield(); Other.Count++; }
                }
                """;
            var run = TypeInitializerTreatmentFixture.Run(source, "GC.KeepAlive(Registry.Value);", cell.Treatment);
            var initializers = run.Executions!.Executions.Where(execution => execution.Kind == ExecutionKind.TypeInitializer).ToArray();
            if (cell.Treatment == TypeInitializerTreatment.Off)
            {
                Assert.Empty(initializers);
                Assert.DoesNotContain(run.Executions.Executions, execution => execution.Kind is ExecutionKind.Spawn or ExecutionKind.TimerCallback);
                continue;
            }
            var initializer = Assert.Single(initializers);
            Assert.Equal(cell.Treatment == TypeInitializerTreatment.Inert, initializer.IsInert);
            Assert.Equal(initializer.IsInert, Assert.Single(run.Heap!.TypeInitializers).IsInert);
            if (!work)
                continue;
            var started = Assert.Single(run.Executions.Executions, execution => execution.Kind is ExecutionKind.Spawn or ExecutionKind.TimerCallback);
            Assert.Equal(initializer.Id, started.ParentId);
            Assert.False(started.IsInert);
            Assert.Contains(run.Executions.ExecutionAccesses(), access => access.ExecutionId == started.Id && access.Access.Field.Name == "Count" &&
                                                                         access.Access.Kind == SummaryAccessKind.Store);
            Assert.Contains(run.Collection!.Accesses, access => access.ExecutionId == started.Id && access.Resource.Member.Name == "Count" &&
                                                               access.Operation is AccessOperation.Write or AccessOperation.ReadModifyWrite);
        }
    }
}
