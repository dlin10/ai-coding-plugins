using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using Xunit;
using Xunit.Abstractions;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A visit of an instance without a parent and the visit hosting what an execution makes for that instance besides running its
/// body are equal paths, and the host is not the visit (issue #472): an execution that enters an instance whole and also hosts it, as the
/// starter of work without a body it runs or as the creator of the escaped sequence it enumerates, still makes the accesses of the
/// instance's body.</summary>
public sealed class HostNodeCollisionTests(ITestOutputHelper output)
{
    /// <summary><c>Pump.Loop</c> starts a delegate that is either <c>Loop</c> itself or a member without a body on another object: the
    /// spawn enters the singleton's <c>Loop</c> whole and hosts the bodyless work <c>Loop</c> starts, and the increment it makes there
    /// meets the one of the next spawn.</summary>
    [Fact]
    public void Work_that_may_restart_its_starter_or_run_a_member_without_a_body_on_another_object_collects_the_starters_accesses_in_the_spawn()
    {
        var run = Run(Spawning("_side.Touch"));

        Report(run);
        Assert.NotEmpty(Collisions(run));
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.ReadModifyWrite && KindOf(run, access) == ExecutionKind.Spawn);
        Assert.NotEmpty(run.PairsOn("Hits"));
    }

    /// <summary>The same with the member without a body on <c>Pump</c> itself: its unknown effect reaches <c>Hits</c> as well, and the
    /// increment of the spawn is still its own access.</summary>
    [Fact]
    public void Work_that_may_restart_its_starter_or_run_a_member_without_a_body_on_it_collects_the_starters_accesses_in_the_spawn()
    {
        var run = Run(Spawning("Touch"));

        Report(run);
        Assert.NotEmpty(Collisions(run));
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.ReadModifyWrite && KindOf(run, access) == ExecutionKind.Spawn);
    }

    /// <summary>The control: the other target is a static member with a body, so the spawn enters <c>Loop</c> whole and hosts nothing.</summary>
    [Fact]
    public void Work_that_may_restart_its_starter_or_run_a_static_member_with_a_body_collects_the_starters_accesses_in_the_spawn()
    {
        var run = Run(Spawning("Native.Rest"));

        Report(run);
        Assert.Empty(Collisions(run));
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.ReadModifyWrite && KindOf(run, access) == ExecutionKind.Spawn);
        Assert.NotEmpty(run.PairsOn("Hits"));
    }

    /// <summary><c>Mapper.Map</c> stores a sequence that maps with <c>Map</c> itself, so the sequence escapes and its unknown enumeration
    /// both enters the singleton's <c>Map</c> whole, as the sequence's delegate, and hosts it, as the sequence's creator: the increment
    /// <c>Map</c> makes there is its own access, and meets the next enumeration's.</summary>
    [Fact]
    public void Sequence_whose_creator_is_its_own_delegate_collects_the_creators_accesses_in_its_unknown_enumeration()
    {
        var run = Run(Enumerating());

        Report(run);
        Assert.NotEmpty(Collisions(run));
        Assert.Contains(run.Of("Hits"), access => access.Operation == AccessOperation.ReadModifyWrite &&
                                                  KindOf(run, access) == ExecutionKind.UnknownEnumeration);
        Assert.NotEmpty(run.PairsOn("Hits"));
    }

    /// <summary>The executions that enter an instance whole and also host it: as the starter of the work without a body the execution
    /// runs, or as the creator of the escaped sequence it enumerates. Those are the pairs of execution and instance whose visit without a
    /// parent is a path equal to the host's.</summary>
    /// <param name="run">The engine run.</param>
    private static IReadOnlyList<(string Execution, string Instance)> Collisions(EngineRun run)
    {
        var heap = run.Execution.Heap.Heap;
        var analysis = run.Execution.Analysis;
        var bodyless = heap.UnresolvedWork.Where(work => work.Site.Kind != UnresolvedWorkKind.Handoff)
                           .Select(work => (work.Site.Instance, work.Site.OperationId)).ToHashSet();
        var starters = analysis.WorkSites.SelectMany(pair => pair.Value.Where(bodyless.Contains).Select(site => (Execution: pair.Key, site.Instance)));
        var creators = analysis.Executions.Where(execution => execution.Kind == ExecutionKind.UnknownEnumeration && execution.Subject is not null &&
                                                              heap.LibrarySequences.ContainsKey(execution.Subject))
                               .Select(execution => (Execution: execution.Id, Instance: heap.LibrarySequences[execution.Subject!].CreatorInstance));
        return starters.Concat(creators)
                       .Where(host => analysis.Entries.GetValueOrDefault(host.Execution, [])
                                              .Any(entry => entry.InstanceId == host.Instance && entry.Segment == BodySegment.Whole))
                       .Distinct().ToArray();
    }

    /// <summary>Prints the entries and work sites of the spawns and unknown enumerations, the bodyless work, the collisions, and the
    /// accesses and pairs of <c>Hits</c>.</summary>
    /// <param name="run">The engine run.</param>
    private void Report(EngineRun run)
    {
        var analysis = run.Execution.Analysis;
        foreach (var execution in analysis.Executions.Where(execution => execution.Kind is ExecutionKind.Spawn or ExecutionKind.UnknownEnumeration))
        {
            output.WriteLine($"execution {execution.Id}");
            foreach (var entry in analysis.Entries.GetValueOrDefault(execution.Id, []))
                output.WriteLine($"  entry {entry.Kind} {entry.InstanceId} {entry.Segment}");
            foreach (var site in analysis.WorkSites.GetValueOrDefault(execution.Id) ?? new HashSet<(string, int)>())
                output.WriteLine($"  work site {site.Instance}#{site.Operation}");
        }

        foreach (var work in run.Execution.Heap.Heap.UnresolvedWork)
            output.WriteLine($"bodyless work {work.Site.Kind} {work.Site.Instance}#{work.Site.OperationId} {work.Site.Callee}");
        foreach (var collision in Collisions(run))
            output.WriteLine($"collision {collision.Execution} {collision.Instance}");
        foreach (var access in run.Of("Hits"))
            output.WriteLine($"Hits {access.Operation} in {KindOf(run, access)} {access.ExecutionId} held [{string.Join(", ", access.HeldProtection)}]");
        output.WriteLine($"pairs on Hits: {run.PairsOn("Hits").Count}");
    }

    /// <summary>The kind of the execution an access is made in.</summary>
    /// <param name="run">The engine run.</param>
    /// <param name="access">The access.</param>
    private static ExecutionKind KindOf(EngineRun run, Access access) => run.Execution.Analysis.Execution(access.ExecutionId).Kind;

    /// <summary>Analyzes a case's scope.</summary>
    /// <param name="source">The source file of the case, after the usings.</param>
    private static EngineRun Run(string source) =>
        AnalyzeScope(FixtureSolution.Create(new FixtureOptions(), ("Case.cs", Usings + source)), "scope:Fixture");

    /// <summary>A singleton <c>Pump</c> whose <c>Loop</c>, which a worker calls, increments <c>Hits</c> and starts either <c>Loop</c>
    /// again or <paramref name="other"/>.</summary>
    /// <param name="other">The other target of the delegate <c>Loop</c> starts.</param>
    private static string Spawning(string other) => $$"""
        using System.Runtime.CompilerServices;

        public sealed class Pump
        {
            public int Hits;
            public bool Stop;
            private readonly Side _side = new();

            public void Loop()
            {
                Hits++;
                Action next = Stop ? {{other}} : Loop;
                Task.Run(next);
            }

            [MethodImpl(MethodImplOptions.InternalCall)]
            public extern void Touch();
        }

        public sealed class Side
        {
            public int Mark;
            [MethodImpl(MethodImplOptions.InternalCall)]
            public extern void Touch();
        }

        public static class Native
        {
            public static void Rest() { }
        }

        public sealed class Worker(Pump pump) : BackgroundService
        {
            private readonly Pump _pump = pump;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                _pump.Loop();
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<Pump>(); services.AddHostedService<Worker>();");

    /// <summary>A singleton <c>Mapper</c> whose <c>Map</c>, which a worker calls, increments <c>Hits</c> and keeps in a field a sequence
    /// that maps with <c>Map</c>.</summary>
    private static string Enumerating() => """
        using System.Collections.Generic;
        using System.Linq;

        public sealed class Mapper
        {
            public int Hits;
            public IEnumerable<int> Kept = new List<int>();

            public int Map(int value)
            {
                Hits++;
                Kept = new List<int> { value }.Select(Map);
                return value;
            }
        }

        public sealed class Worker(Mapper mapper) : BackgroundService
        {
            private readonly Mapper _mapper = mapper;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                _mapper.Map(1);
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<Mapper>(); services.AddHostedService<Worker>();");
}
