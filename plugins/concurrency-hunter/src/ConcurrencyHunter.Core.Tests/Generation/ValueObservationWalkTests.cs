using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>A query of <c>ValueObservation</c> visits each value once: over a dense cycle of captured variables its work grows with the
/// square of their number, where a walk of every simple path grows with their factorial; and a value met again, on another branch or by
/// another query, hides nothing that lies beyond it.</summary>
public sealed class ValueObservationWalkTests
{
    private const string MEMBER = "M:Lib.Api.Run";

    /// <summary>A call the engine cannot follow: what it gives is an object the heap does not name.</summary>
    private const string EXTERNALS = "public static class Externals { public static extern Box MakeBox(); }\n";

    [Fact]
    public void A_dense_cycle_of_captured_variables_is_walked_once_per_variable()
    {
        // One lambda assigns every variable every other: from v0, a walk of every simple path goes through about e·7! of them.
        const int VARIABLES = 8;
        var names = Enumerable.Range(0, VARIABLES).Select(index => $"v{index}").ToArray();
        var assignments = names.SelectMany(target => names.Where(source => source != target).Select(source => $"{target} = {source};"));
        var observed = new Observed($$"""
            public static class Api
            {
                public static Box Run()
                {
                    Box {{string.Join(", ", names.Select(name => $"{name} = new Box()"))}};
                    Action shuffle = () => { {{string.Join(" ", assignments)}} };
                    shuffle();
                    return v0;
                }
            }
            """, MEMBER);

        var (unobserved, checks) = observed.Query(observed.Member, Observed.Returned(observed.Member));

        Assert.False(unobserved);
        // Each variable once, scanning the writes into its cell: a few checks for each of the VARIABLES² writes.
        Assert.InRange(checks, 1, 8 * VARIABLES * VARIABLES);
    }

    [Fact]
    public void A_value_met_again_on_another_branch_hides_nothing_beyond_it()
    {
        // a reaches p through b and through c, and p reaches back to a; only p is given an object the heap does not name. Whichever
        // branch a walk takes first, the other meets p already visited, and so does a value p reaches back through.
        var observed = new Observed("""
            public static class Api
            {
                public static Box Run()
                {
                    Box a = new Box(), b = new Box(), c = new Box(), p = new Box();
                    Action shuffle = () => { a = b; a = c; b = p; c = p; p = a; p = Externals.MakeBox(); };
                    shuffle();
                    return a;
                }
            }
            """, MEMBER);

        Assert.True(observed.Query(observed.Member, Observed.Returned(observed.Member)).Unobserved);
        var shuffle = observed.Instances.Single(instance => instance != observed.Member && instance.Summary.CapturedStores.Count != 0);
        var stored = shuffle.Summary.CapturedStores.SelectMany(store => store.Values).Distinct().ToArray();
        Assert.NotEmpty(stored);
        Assert.All(stored, value => Assert.True(observed.Query(shuffle, [value]).Unobserved, value.ToString()));
    }

    [Fact]
    public void A_task_one_query_walked_is_walked_again_by_the_next()
    {
        // Both results wait for the one task, which completes with an object the heap does not name: each its own query.
        var observed = new Observed("""
            public static class Api
            {
                public static Box Run(bool flag)
                {
                    var task = Task.Run(() => Externals.MakeBox());
                    var first = task.Result;
                    var second = task.Result;
                    return flag ? first : second;
                }
            }
            """, MEMBER + "(System.Boolean)");

        var returned = Observed.Returned(observed.Member);
        Assert.True(returned.Count >= 2, string.Join(", ", returned));
        Assert.All(returned, value => Assert.True(observed.Query(observed.Member, [value]).Unobserved, value.ToString()));
    }

    /// <summary>A fixture member's generator run, and an observation of it that counts the checks its walks make: one at every value,
    /// task, write and edge a walk goes through.</summary>
    private sealed class Observed
    {
        private readonly ValueObservation _observation;
        private int _checks;

        /// <summary>Runs the generator on a member of a library of the prelude, <see cref="EXTERNALS"/> and some types.</summary>
        /// <param name="types">Declarations inside namespace <c>Lib</c>.</param>
        /// <param name="member">The member's declaration id.</param>
        public Observed(string types, string member)
        {
            var trace = GenerationRuns.Trace(EXTERNALS + types, member);
            Assert.True(trace.Driver is not null && trace.Run?.Heap is not null, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
            Instances = trace.Run.Heap.Instances.Values.ToArray();
            Member = Instances.Single(instance => instance.BodyId == IrLowering.RootBodyId(trace.Driver.Member));
            var unknownCalls = GenerationHandoffs.UnfollowedCalls(trace.Run).Select(call => (call.Instance.Id, call.OperationId)).ToHashSet();
            _observation = new ValueObservation(trace.Driver, trace.Run, unknownCalls,
                                                new StageChecks(new GenerationChecks(CancellationToken.None, (_, _) => _checks++),
                                                                GenerationStage.ReadEffects));
        }

        public IReadOnlyList<MethodInstance> Instances { get; }

        /// <summary>The member's own instance.</summary>
        public MethodInstance Member { get; }

        /// <summary>The alternatives an instance returns.</summary>
        /// <param name="instance">The instance.</param>
        public static IReadOnlyList<AbstractValue> Returned(MethodInstance instance) =>
            instance.Summary.Returns.SelectMany(returned => returned.Values).Distinct().ToArray();

        /// <summary>One query: whether some alternative may be an object the heap does not name, and the checks its walk made.</summary>
        /// <param name="instance">The instance expressing the alternatives.</param>
        /// <param name="values">The alternatives.</param>
        public (bool Unobserved, int Checks) Query(MethodInstance instance, IReadOnlyList<AbstractValue> values)
        {
            _checks = 0;
            var unobserved = _observation.HasUnobservedValues(instance, values, ValueOrigin.None);
            return (unobserved, _checks);
        }
    }
}
