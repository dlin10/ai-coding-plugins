using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>A generation observes its token in every stage (<see cref="GenerationStage"/>), at the stage's start and at each boundary of
/// its repeated work. The tests cancel from the callback that reports each check, so no test is timed against the machine's speed.</summary>
public sealed class ModelGeneratorCancellationTests : IDisposable
{
    private const string PACKAGE = "Fixture.Cancel";

    /// <summary>One top-level type, so that the decompilation has one worker's check to cancel at and no other worker's to race it. The
    /// constructor holds a delegate its type fires, so the generation reaches the holder's confirmation.</summary>
    private const string SOURCE = """
        using System;
        namespace Lib
        {
            public sealed class Holder
            {
                private readonly Action _action;
                public Holder(Action action) { _action = action; }
                public void Fire() { _action(); }
            }
        }
        """;

    private const string HOLDER = "M:Lib.Holder.#ctor(System.Action)";

    /// <summary>The stages the fixture's member runs through, each to a second check.</summary>
    private static readonly GenerationStage[] Reached =
    [
        GenerationStage.Resolve, GenerationStage.CompileLibrary, GenerationStage.EngineClaims, GenerationStage.Synthesize,
        GenerationStage.Analyze, GenerationStage.Classify, GenerationStage.ConfirmHolder, GenerationStage.ReadEffects, GenerationStage.BuildModel
    ];

    private readonly GenerationInstall _install = new();

    public ModelGeneratorCancellationTests()
    {
        _install.SharedFramework("8.0.5", runtime: true);
        var package = _install.Package(PACKAGE, "1.0.0", "", $"net8.0/{PACKAGE}");
        EmittedAssemblies.Write(Path.Combine(package, "lib", "net8.0", PACKAGE + ".dll"), PACKAGE, SOURCE);
    }

    /// <summary>The name of every stage; the stage type is internal, so a test method names it by its name.</summary>
    public static TheoryData<string> Stages() => new(Enum.GetNames<GenerationStage>());

    [Theory]
    [MemberData(nameof(Stages))]
    public void Cancellation_at_each_stage_stops_the_generation(string name)
    {
        var stage = Member(name);

        var (seen, error) = Generate(stage, 1);

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal((stage, 1), seen[^1]);
        Assert.DoesNotContain(seen, check => check.Stage > stage);
    }

    [Theory]
    [MemberData(nameof(Stages))]
    public void Cancellation_inside_each_stage_stops_the_generation(string name)
    {
        var stage = Member(name);

        var (seen, error) = Generate(stage, 2);

        // The fixture reaches a second check of every stage: the call would otherwise end without a cancellation.
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal((stage, 2), seen[^1]);
        Assert.DoesNotContain(seen, check => check.Stage == stage && check.Ordinal > 2);
        Assert.DoesNotContain(seen, check => check.Stage > stage);
    }

    [Fact]
    public void Cancelled_token_resolves_nothing()
    {
        var reads = new List<string>();
        var seen = new List<(GenerationStage Stage, int Ordinal)>();

        var error = Record.Exception(() => ModelGenerator.Trace(Request, _install.Resolver(nuspecRead: reads.Add), new CancellationToken(true),
                                                                (stage, ordinal) => seen.Add((stage, ordinal))));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Empty(reads);
        Assert.Equal((GenerationStage.Resolve, 1), Assert.Single(seen));
    }

    private const string SEND_TYPES = "public abstract class Node { public object Next; } public static class Api { public static void Send(Node n, Action a) => Sink.Take(n); }";

    private const string SEND = "M:Lib.Api.Send(Lib.Node,System.Action)";

    [Fact]
    public void Cancellation_inside_the_reachability_stops_each_of_its_walks()
    {
        var trace = GenerationRuns.Trace(SEND_TYPES, SEND);
        var heap = trace.Run!.Heap!;
        var root = Assert.Single(GenerationRuns.Slot(trace, DriverSynthesizer.DRIVER_TYPE, "Arg_n_Call"));
        using var cancellation = new CancellationTokenSource();
        var stage = new StageChecks(new GenerationChecks(cancellation.Token), GenerationStage.Classify);
        var observing = new HeapReachability(heap, null, stage);
        var plain = new HeapReachability(heap);
        var reached = observing.From([root]);
        var start = Assert.Single(observing.StaticFields(), field => field.Slot == HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.DRIVER_TYPE, "Arg_n_Call"));

        Assert.True(reached.Count > 1);
        Assert.True(reached.SetEquals(plain.From([root])));
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => observing.From([root]));
        Assert.Throws<OperationCanceledException>(() => observing.VisibleFrom([root]));
        Assert.Throws<OperationCanceledException>(() => observing.Reaches(start, reached, null, counted: true, new HashSet<string>()));
        Assert.Throws<OperationCanceledException>(() => observing.StaticFields());
        Assert.Throws<OperationCanceledException>(() => observing.Edges(root));
        // A reachability built without the stage's checks is no part of the generation: the token is not its.
        Assert.True(reached.SetEquals(plain.From([root])));
        Assert.True(reached.SetEquals(new HeapReachability(heap, null, default).From([root])));
    }

    [Fact]
    public void Cancellation_inside_the_handoffs_stops_their_construction_between_its_scans()
    {
        var trace = GenerationRuns.Trace(SEND_TYPES, SEND);
        var run = trace.Run!;
        var reachability = new HeapReachability(run.Heap!);
        var result = reachability.StaticField(HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, DriverSynthesizer.KEEP_TYPE, "R"));
        var executions = new DriverExecutions(trace.Driver!, run.Executions!, reachability.From(result is null ? [] : reachability.Targets(result)));
        var plain = new GenerationHandoffs(trace.Driver!, run, executions, reachability);
        using var cancellation = new CancellationTokenSource();
        var seen = new List<(GenerationStage Stage, int Ordinal)>();
        var checks = new GenerationChecks(cancellation.Token, (stage, ordinal) =>
        {
            seen.Add((stage, ordinal));
            if (ordinal == 3)
                cancellation.Cancel();
        });

        var error = Record.Exception(() => new GenerationHandoffs(trace.Driver!, run, executions, reachability,
                                                                   new StageChecks(checks, GenerationStage.Classify)));

        // The construction checks at each call, instance and access it goes through, not only at its start and its end.
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal((GenerationStage.Classify, 3), seen[^1]);
        var unobserved = new GenerationHandoffs(trace.Driver!, run, executions, reachability, default);
        Assert.True(unobserved.HandedOutsideSetup.SetEquals(plain.HandedOutsideSetup));
        Assert.True(unobserved.ForeignAccesses.SetEquals(plain.ForeignAccesses));
    }

    private static GenerationRequest Request => new(PACKAGE, "1.0.0", HOLDER, PACKAGE, "net8.0");

    /// <summary>The stage of a name the theory gives; a stage the tests do not know fails them rather than passing unchecked.</summary>
    /// <param name="name">The stage's name.</param>
    private static GenerationStage Member(string name)
    {
        var stage = Enum.Parse<GenerationStage>(name);
        return Reached.Contains(stage) ? stage : throw new ArgumentOutOfRangeException(nameof(name), name, "no fixture member reaches this stage");
    }

    /// <summary>Generates the fixture's member, cancelling at one check of one stage.</summary>
    /// <param name="stage">The stage to cancel in.</param>
    /// <param name="ordinal">The ordinal of the check within that stage to cancel at.</param>
    private (List<(GenerationStage Stage, int Ordinal)> Seen, Exception? Error) Generate(GenerationStage stage, int ordinal)
    {
        using var cancellation = new CancellationTokenSource();
        var seen = new List<(GenerationStage Stage, int Ordinal)>();
        void Report(GenerationStage reported, int count)
        {
            lock (seen)
                seen.Add((reported, count));
            if (reported == stage && count == ordinal)
                cancellation.Cancel();
        }

        var error = Record.Exception(() => ModelGenerator.Trace(Request, _install.Resolver(), cancellation.Token, Report));
        // The compilation's worker may report a check as the call returns: the list is read under the lock it is written under.
        lock (seen)
            return (seen.ToList(), error);
    }

    public void Dispose() => _install.Dispose();
}
