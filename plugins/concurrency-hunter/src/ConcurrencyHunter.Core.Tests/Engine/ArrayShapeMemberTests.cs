using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ArrayShapeMemberTests
{
    [Fact]
    public void Array_length_is_a_known_call_without_access()
    {
        var run = Run("var values = _state.Slots; var length = values.Length;");
        var without = Run("var values = _state.Slots; var length = 0;");

        AssertKnownWithoutAccess(run, without, 1);
    }

    [Fact]
    public void LongLength_and_GetLongLength_are_known_calls_without_access()
    {
        var run = Run("System.Array values = _state.Slots; var length = values.LongLength; var dimension = values.GetLongLength(0);");
        var without = Run("System.Array values = _state.Slots; var length = 0L; var dimension = 0L;");

        AssertKnownWithoutAccess(run, without, 2);
    }

    [Fact]
    public void Rank_and_bounds_are_known_calls_without_access()
    {
        var run = Run("System.Array values = _state.Slots; var rank = values.Rank; var lower = values.GetLowerBound(0); " +
                      "var upper = values.GetUpperBound(0);");
        var without = Run("System.Array values = _state.Slots; var rank = 0; var lower = 0; var upper = 0;");

        AssertKnownWithoutAccess(run, without, 3);
    }

    [Fact]
    public void Multidimensional_GetLength_is_known()
    {
        var run = Run("var values = _state.Grid; var length = values.GetLength(1);");
        var without = Run("var values = _state.Grid; var length = 0;");

        AssertKnownWithoutAccess(run, without, 1);
    }

    [Fact]
    public async Task Length_on_a_shared_array_makes_no_gap()
    {
        var result = await PairScenario("_board.Slots.Length");

        Assert.DoesNotContain(result.Coverage.SelectMany(coverage => coverage.Gaps),
                              gap => gap.Sites.Any(site => site.BodyId.Contains("SlotController.Post", StringComparison.Ordinal) ||
                                                           site.BodyId.Contains("SlotWorker.ExecuteAsync", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Loop_bound_on_Length_adds_no_pair_to_the_element_increment()
    {
        var withLength = await PairScenario("_board.Slots.Length");
        var withConstant = await PairScenario("SlotBoard.Size");

        Assert.Equal(2, withLength.Findings.Count);
        Assert.Equal(withConstant.Findings.Select(finding => finding.Fingerprint).Order(StringComparer.Ordinal),
                     withLength.Findings.Select(finding => finding.Fingerprint).Order(StringComparer.Ordinal));
        Assert.All(withLength.Findings, finding => Assert.Equal(("DCA1002", "High"), (finding.RuleId, finding.Confidence.Label)));
    }

    private static void AssertKnownWithoutAccess(EngineRun run, EngineRun without, int calls)
    {
        Assert.Equal(calls, run.Counter(CoverageCounters.KNOWN_CALL) - without.Counter(CoverageCounters.KNOWN_CALL));
        Assert.Equal(0, run.Counter(CoverageCounters.OPAQUE_CALL) - without.Counter(CoverageCounters.OPAQUE_CALL));
        Assert.Equal(Accesses(without), Accesses(run));
    }

    private static string[] Accesses(EngineRun run) =>
        run.Collection.Accesses.Select(access => $"{access.Symbol} {access.Operation} {access.Resource.Region}.{string.Join('.', access.Resource.AccessPath)}")
           .Order(StringComparer.Ordinal).ToArray();

    private static EngineRun Run(string work) => AnalyzeScope(FixtureSolution.Create(("Case.cs", Usings + $$"""
        public sealed class State
        {
            public int[] Slots = new int[8];
            public int[,] Grid = new int[2, 3];
        }

        public sealed class Worker : BackgroundService
        {
            private readonly State _state;
            public Worker(State state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();"))), "scope:Fixture");

    private static Task<AnalysisResult> PairScenario(string bound) => PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Usings + $$"""
        public sealed class SlotBoard
        {
            public const int Size = 8;
            public int[] Slots { get; } = new int[Size];
        }

        [ApiController]
        public sealed class SlotController : ControllerBase
        {
            private readonly SlotBoard _board;
            public SlotController(SlotBoard board) => _board = board;
            [HttpPost("/slots")]
            public void Post(int i)
            {
                if (i < {{bound}})
                    _board.Slots[i]++;
            }
        }

        public sealed class SlotWorker : BackgroundService
        {
            private readonly SlotBoard _board;
            public SlotWorker(SlotBoard board) => _board = board;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                for (var i = 0; i < {{bound}}; i++)
                    _board.Slots[i]++;
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<SlotBoard>(); services.AddHostedService<SlotWorker>();"))), ROOT_DIRECTORY, CancellationToken.None);
}
