using System.Text;
using ConcurrencyHunter.Heap;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The task-result matrix of the run (R4, R2): one fixture of project-model members, analysed once, with one controller action
/// per cell and one per twin. A result cell crosses the leaf a <c>task(…)</c> result completes with, how many tasks stand around it,
/// the outer task type and the consumer; its twin is the same member with the outer task taken out, consumed once fewer. A channel cell
/// delivers <c>completion(arg:t)</c> through one channel of a call for a task t completing with a known, an opaque or a mixed value; an
/// isolation cell delivers two such values, one known and one opaque, through two channels of one call; a synchronous cell delivers it
/// as the result of a member returning <c>T</c>. Their twins name <c>arg:x</c> for the value x the task completes with. Each delivered
/// value is judged where a consumer reads it, by a dispatch on it. Every cell must be exact against its twin, but the channels of the
/// cells <see cref="SynchronousTwinRule"/> lists, which are exact against what their task completes with and wider than the twin.</summary>
public sealed class TaskResultRunMatrixTests
{
    private const string FLAG = "Environment.ProcessorCount > 1";

    private const string CELL_PREFIX = "body:Fixture:M:Cell_";

    private static readonly Lazy<Matrix> Results = new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    // ---- the axes ----

    /// <summary>The innermost result a <c>task(…)</c> result completes with.</summary>
    public enum Leaf
    {
        New,
        Argument,
        Collection,
        Dictionary,
        Sequence,
        Completion
    }

    /// <summary>The outer task type of a result cell.</summary>
    public enum Outer
    {
        Task,
        ValueTask
    }

    /// <summary>How a task is consumed, once per depth.</summary>
    public enum Consumer
    {
        Await,
        Result
    }

    /// <summary>Where a call delivers a <c>completion(…)</c> value.</summary>
    public enum Channel
    {
        Result,
        Input,
        Keeps,
        Stores,
        Outputs
    }

    /// <summary>What the task a <c>completion(…)</c> value names completes with.</summary>
    public enum Completion
    {
        Known,
        Opaque,
        Mixed
    }

    /// <summary>A result cell.</summary>
    /// <param name="Leaf">The leaf.</param>
    /// <param name="Depth">How many tasks stand around the leaf: 1 or 2.</param>
    /// <param name="Outer">The outer task type.</param>
    /// <param name="Consumer">The consumer, applied once per depth.</param>
    private sealed record ResultCell(Leaf Leaf, int Depth, Outer Outer, Consumer Consumer)
    {
        public string Name => $"R_{Leaf}_{Depth}_{Outer}_{Consumer}";
    }

    /// <summary>One channel of a call delivering the value of one source: <c>completion(arg:tN)</c> in a cell, <c>arg:xN</c> in a twin.</summary>
    /// <param name="Channel">The channel.</param>
    /// <param name="Source">The source's number, 1 or 2.</param>
    private sealed record Use(Channel Channel, int Source);

    /// <summary>A channel or isolation cell: the channels of its call and what each source completes with.</summary>
    /// <param name="Name">The cell's name.</param>
    /// <param name="Uses">The call's channels, in their order within each kind.</param>
    /// <param name="Completions">What each source completes with, by source number less one.</param>
    /// <param name="Synchronous">Whether the call's result is the delivered value itself, <c>[completion(arg:t)]</c> on a member returning
    /// <c>T</c>, rather than a task completing with it.</param>
    private sealed record ChannelCell(string Name, IReadOnlyList<Use> Uses, IReadOnlyList<Completion> Completions, bool Synchronous = false)
    {
        public string TwinName => "TW" + Name;
    }

    // ---- the cells ----

    private static IEnumerable<ResultCell> ResultCells() =>
        from leaf in Enum.GetValues<Leaf>()
        from depth in new[] { 1, 2 }
        from outer in Enum.GetValues<Outer>()
        from consumer in Enum.GetValues<Consumer>()
        select new ResultCell(leaf, depth, outer, consumer);

    private static IEnumerable<ChannelCell> ChannelCells() =>
        from channel in Enum.GetValues<Channel>()
        from completion in Enum.GetValues<Completion>()
        select new ChannelCell($"C_{channel}_{completion}", [new Use(channel, 1)], [completion]);

    /// <summary>The result channel with no task around the delivered value: its twin is <c>[arg:x]</c> on a member returning <c>T</c>.</summary>
    private static IEnumerable<ChannelCell> SynchronousCells() =>
        from completion in Enum.GetValues<Completion>()
        select new ChannelCell($"S_Result_{completion}", [new Use(Channel.Result, 1)], [completion], Synchronous: true);

    /// <summary>Every pair of two different channels, and two of one kind for the kinds a call can have twice, each once as stated (the
    /// first channel delivers t1's known value, the second t2's opaque one) and once exchanged.</summary>
    private static IEnumerable<ChannelCell> IsolationCells()
    {
        var channels = Enum.GetValues<Channel>();
        var pairs = new List<(Channel First, Channel Second)>();
        for (var first = 0; first < channels.Length; first++)
        for (var second = first + 1; second < channels.Length; second++)
            pairs.Add((channels[first], channels[second]));
        pairs.AddRange([(Channel.Outputs, Channel.Outputs), (Channel.Input, Channel.Input), (Channel.Keeps, Channel.Keeps), (Channel.Stores, Channel.Stores)]);
        foreach (var (first, second) in pairs)
        {
            yield return new ChannelCell($"I_{first}_{second}_Stated", [new Use(first, 1), new Use(second, 2)], [Completion.Known, Completion.Opaque]);
            yield return new ChannelCell($"I_{first}_{second}_Exchanged", [new Use(first, 2), new Use(second, 1)], [Completion.Known, Completion.Opaque]);
        }
    }

    // ---- the tests ----

    [Fact]
    public void Every_axis_value_occurs_and_the_matrix_has_91_cells()
    {
        var results = ResultCells().ToArray();
        var channels = ChannelCells().ToArray();
        var isolation = IsolationCells().ToArray();
        Assert.Equal(48, results.Length);
        Assert.Equal(15, channels.Length);
        Assert.Equal(28, isolation.Length);
        Assert.Equal(91, results.Length + channels.Length + isolation.Length);
        Assert.All(Enum.GetValues<Leaf>(), leaf => Assert.Contains(results, cell => cell.Leaf == leaf));
        Assert.All(new[] { 1, 2 }, depth => Assert.Contains(results, cell => cell.Depth == depth));
        Assert.All(Enum.GetValues<Outer>(), outer => Assert.Contains(results, cell => cell.Outer == outer));
        Assert.All(Enum.GetValues<Consumer>(), consumer => Assert.Contains(results, cell => cell.Consumer == consumer));
        Assert.All(Enum.GetValues<Channel>(), channel => Assert.Contains(channels, cell => cell.Uses[0].Channel == channel));
        Assert.All(Enum.GetValues<Completion>(), completion => Assert.Contains(channels, cell => cell.Completions[0] == completion));
        // Ten pairs of two kinds and four of one kind, each stated and exchanged.
        Assert.Equal(20, isolation.Count(cell => cell.Uses[0].Channel != cell.Uses[1].Channel));
        Assert.Equal(8, isolation.Count(cell => cell.Uses[0].Channel == cell.Uses[1].Channel));
        Assert.Equal(isolation.Length, isolation.Select(cell => cell.Name).Distinct().Count());
        // Every cell and every twin is a root of the run, and nothing else of the controller is.
        var roots = Results.Value.Heap.Program.Input.Roots
                           .Select(root => root.Entry.Symbol)
                           .Where(symbol => symbol.StartsWith("MatrixController.", StringComparison.Ordinal))
                           .Select(symbol => symbol["MatrixController.".Length..^"()".Length])
                           .Order(StringComparer.Ordinal);
        Assert.Equal(Actions().Order(StringComparer.Ordinal), roots);
    }

    [Fact]
    public void Every_result_cell_is_exact()
    {
        var failures = ResultCells().SelectMany(cell => Compare(cell).Select(failure => $"{cell.Name}: {failure}")).ToArray();
        Assert.True(failures.Length == 0, $"{failures.Length} result cell failure(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_channel_cell_is_exact()
    {
        var failures = ChannelCells().SelectMany(cell => Compare(cell).Select(failure => $"{cell.Name}: {failure}")).ToArray();
        Assert.True(failures.Length == 0, $"{failures.Length} channel cell failure(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_synchronous_completion_result_is_consumed_as_its_twin()
    {
        var failures = SynchronousCells().SelectMany(cell => Compare(cell).Select(failure => $"{cell.Name}: {failure}")).ToArray();
        Assert.True(failures.Length == 0, $"{failures.Length} synchronous cell failure(s):\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Every_isolation_cell_is_exact()
    {
        var failures = IsolationCells().SelectMany(cell => Compare(cell).Select(failure => $"{cell.Name}: {failure}")).ToArray();
        Assert.True(failures.Length == 0, $"{failures.Length} isolation cell failure(s):\n" + string.Join("\n", failures));
    }

    // ---- the expectation and comparing ----

    /// <summary>The roles the consumed value of a result cell holds by R4: the leaf's objects, and one level of what a created collection,
    /// dictionary or sequence holds.</summary>
    /// <param name="leaf">The leaf.</param>
    private static IReadOnlySet<string> ExpectedRoles(Leaf leaf) => leaf switch
    {
        Leaf.New => Set("created:Fixture:Box"),
        Leaf.Argument or Leaf.Completion => Set("Shared"),
        Leaf.Collection => Set("created:System.Private.CoreLib:System.Collections.Generic.List<Fixture:Box>", "element:Shared", "element:Other"),
        Leaf.Dictionary => Set("created:System.Private.CoreLib:System.Collections.Generic.Dictionary<Fixture:Box, Fixture:Box>", "key:Other", "element:Shared"),
        Leaf.Sequence => Set("sequence", "element:Shared", "element:Other"),
        _ => throw new ArgumentOutOfRangeException(nameof(leaf), leaf, null)
    };

    private static IEnumerable<string> Compare(ResultCell cell)
    {
        var observed = Observe(cell.Name);
        var twin = Observe(TwinName(cell));
        foreach (var failure in Roles("against the twin", observed.Roles, twin.Roles))
            yield return failure;
        foreach (var failure in Roles("by R4", observed.Roles, ExpectedRoles(cell.Leaf)))
            yield return failure;
        if (observed.Touches == 0 || twin.Touches == 0)
            yield return $"no dispatch on the consumed value (cell {observed.Touches}, twin {twin.Touches})";
        if (observed.Unfollowed != twin.Unfollowed)
            yield return $"unfollowed {observed.Unfollowed}, twin {twin.Unfollowed}";
        // Where the consumed value is the box itself, nothing the leaf names is unknown; an element read is an unknown source of its own,
        // in the cell and the twin alike.
        if (observed.Unfollowed && cell.Leaf is Leaf.New or Leaf.Argument or Leaf.Completion)
            yield return "unfollowed, though every value the leaf names is known";
        if (!observed.Runs.SetEquals(twin.Runs))
            yield return $"iterator runs at [{string.Join(",", observed.Runs)}], twin at [{string.Join(",", twin.Runs)}]";
        if (cell.Leaf == Leaf.Sequence && !observed.Runs.SetEquals(["Run"]))
            yield return $"iterator runs at [{string.Join(",", observed.Runs)}], expected where the value is enumerated, [Run]";
        if (cell.Leaf != Leaf.Sequence && observed.Runs.Count != 0)
            yield return $"a delegate runs at [{string.Join(",", observed.Runs)}], expected none";
        var outer = Assert.Single(observed.Tasks, task => task.Variable == "t");
        if (outer.Regions.Count != 1 || outer.Regions.Any(region => !region.IsTask))
            yield return $"the call's result is [{string.Join(",", outer.Regions.Select(region => region.Identity))}], expected one task region";
        if (cell.Depth == 2)
        {
            var inner = Assert.Single(observed.Tasks, task => task.Variable == "inner");
            if (inner.Regions.Count != 1 || inner.Regions.Any(region => !region.IsTask) ||
                inner.Regions.Select(region => region.Identity).Intersect(outer.Regions.Select(region => region.Identity)).Any())
                yield return $"the first consumption is [{string.Join(",", inner.Regions.Select(region => region.Identity))}], expected one task " +
                             $"region other than the call's [{string.Join(",", outer.Regions.Select(region => region.Identity))}]";
        }
    }

    private static IEnumerable<string> Compare(ChannelCell cell)
    {
        var observed = Observe(cell.Name);
        var twin = Observe(cell.TwinName);
        var flagged = observed.Flagged;
        var sources = cell.Uses.Select(use => use.Source).ToArray();
        foreach (var use in cell.Uses)
        {
            var variable = $"g{use.Source}";
            var completion = cell.Completions[use.Source - 1];
            var roles = observed.Values.GetValueOrDefault(variable) ?? new HashSet<string>(StringComparer.Ordinal);
            var twinRoles = twin.Values.GetValueOrDefault(variable) ?? new HashSet<string>(StringComparer.Ordinal);
            foreach (var failure in Roles($"{variable} against the twin", roles, twinRoles))
                yield return failure;
            foreach (var failure in Roles($"{variable} by R4", roles, completion == Completion.Opaque ? Set() : Set("Shared")))
                yield return failure;
            // R2: the channel's value is unfollowed exactly when the value the task completes with, handed over directly, is.
            var expected = twin.SourcesUnfollowed.Contains(use.Source);
            if (expected != (completion != Completion.Known))
                yield return $"twin x{use.Source} unfollowed {expected}, though it is {completion}";
            var channel = ChannelName(cell.Uses, use);
            if (flagged.Contains(channel) != expected)
                yield return $"{variable} through {channel} unfollowed {flagged.Contains(channel)}, expected {expected}";
            // What a consumer of the delivered value sees: the dispatch on it, and whether the value it reads is unfollowed, against the
            // twin's same dispatch through the same channel.
            var delivered = observed.Delivered.GetValueOrDefault(use.Source);
            var twinDelivered = twin.Delivered.GetValueOrDefault(use.Source);
            if (delivered is null || twinDelivered is null)
            {
                yield return $"no dispatch on {variable} (cell {delivered is not null}, twin {twinDelivered is not null})";
                continue;
            }
            if (SynchronousTwinRule.Contains(cell.Name) && use.Channel is Channel.Result or Channel.Keeps && completion != Completion.Known)
            {
                // The twin is narrower here by the synchronous rule: the cell is consumed as the value its task completes with is, and
                // wider than the twin.
                if (delivered.Unfollowed != expected || delivered.Unresolved != expected)
                    yield return $"{variable} through {channel} is consumed as {delivered}, though the task completes with a {completion} value";
                if (!(delivered.Unfollowed && !twinDelivered.Unfollowed || delivered.Unresolved && !twinDelivered.Unresolved))
                    yield return $"{variable} through {channel} is consumed as {delivered}, listed by the synchronous twin rule (catalog question " +
                                 $"134) but no wider than the twin's {twinDelivered}";
            }
            else
            {
                if (delivered != twinDelivered)
                    yield return $"{variable} through {channel} is consumed as {delivered}, the twin's as {twinDelivered}";
                if (use.Channel is Channel.Result or Channel.Keeps)
                {
                    if (delivered.Unfollowed != expected || delivered.Unresolved != expected)
                        yield return $"{variable} through {channel} is consumed as {delivered}, though the task completes with a {completion} value";
                }
                else if (!twinDelivered.Unfollowed || !twinDelivered.Unresolved)
                    yield return $"the twin's {variable} through {channel}, unfollowed whatever x{use.Source} is, is consumed as {twinDelivered}";
            }
        }

        if (SynchronousTwinRule.Contains(cell.Name) &&
            !cell.Uses.Any(use => use.Channel is Channel.Result or Channel.Keeps && cell.Completions[use.Source - 1] != Completion.Known))
            yield return "listed by the synchronous twin rule (catalog question 134), but no result or keeper channel of it delivers an unknown value";

        var others = flagged.Except(cell.Uses.Select(use => ChannelName(cell.Uses, use))).ToArray();
        if (others.Length != 0)
            yield return $"unfollowed channels [{string.Join(",", others)}] the call has no completion in";
        if (sources.Length != sources.Distinct().Count())
            yield return "two channels deliver one source";
    }

    /// <summary>The cells wider than their twins by the synchronous twin rule (catalog question 134): a modeled call's synchronous result
    /// or kept value keeps no unknown source of the value its entry names, so the twin's <c>[arg:x]</c> through the result of a member
    /// returning <c>T</c> or through a keeper read by <c>[kept:k]</c> is consumed as followed whatever x is. In these cells each result
    /// or keeper channel delivering an opaque or mixed value is exact against what its task completes with and strictly wider than the
    /// twin; every other channel, and every cell not listed, is exact against its twin.</summary>
    private static readonly IReadOnlySet<string> SynchronousTwinRule = new HashSet<string>(StringComparer.Ordinal)
    {
        "S_Result_Opaque", "S_Result_Mixed", "C_Keeps_Opaque", "C_Keeps_Mixed", "I_Result_Keeps_Stated", "I_Input_Keeps_Stated",
        "I_Keeps_Stores_Exchanged", "I_Keeps_Outputs_Exchanged", "I_Keeps_Keeps_Stated", "I_Keeps_Keeps_Exchanged"
    };

    private static IEnumerable<string> Roles(string what, IReadOnlySet<string> observed, IReadOnlySet<string> expected)
    {
        var missing = expected.Except(observed).Order(StringComparer.Ordinal).ToArray();
        var extra = observed.Except(expected).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            yield return $"regions {what}: missing [{string.Join(",", missing)}] of [{string.Join(",", expected.Order(StringComparer.Ordinal))}] (narrower)";
        if (extra.Length > 0)
            yield return $"regions {what}: extra [{string.Join(",", extra)}] beyond [{string.Join(",", expected.Order(StringComparer.Ordinal))}] (wider)";
    }

    private static IReadOnlySet<string> Set(params string[] roles) => roles.ToHashSet(StringComparer.Ordinal);

    // ---- observing ----

    /// <summary>A task region a local holds.</summary>
    /// <param name="Identity">The region.</param>
    /// <param name="IsTask">Whether it is a task region.</param>
    private sealed record TaskRegion(string Identity, bool IsTask);

    /// <summary>The regions of one local that holds a task.</summary>
    /// <param name="Variable">The local.</param>
    /// <param name="Regions">Its regions.</param>
    private sealed record TaskLocal(string Variable, IReadOnlyList<TaskRegion> Regions);

    /// <summary>What a cell's or a twin's action does with the values it is handed.</summary>
    /// <param name="Roles">The roles of the consumed value of a result cell.</param>
    /// <param name="Values">The roles of each delivered local <c>gN</c> of a channel or isolation cell.</param>
    /// <param name="Tasks">The task locals <c>t</c> and <c>inner</c>.</param>
    /// <param name="Unfollowed">Whether a dispatch of the action's own body on the consumed value is marked as reaching an object the heap
    /// does not follow.</param>
    /// <param name="Touches">How many instances of that dispatch the heap has.</param>
    /// <param name="Runs">The methods of the class from which a lambda of it runs.</param>
    /// <param name="Flagged">The channels the heap marks as delivering an unfollowed <c>completion(…)</c> value.</param>
    /// <param name="SourcesUnfollowed">The sources <c>xN</c> of a twin whose value may be an object the analysis does not follow.</param>
    /// <param name="Delivered">How the dispatch <c>TouchN</c> on each delivered local <c>gN</c> consumes it, by source number.</param>
    private sealed record Observed(IReadOnlySet<string> Roles, IReadOnlyDictionary<string, IReadOnlySet<string>> Values, IReadOnlyList<TaskLocal> Tasks,
                                   bool Unfollowed, int Touches, IReadOnlySet<string> Runs, IReadOnlySet<string> Flagged, IReadOnlySet<int> SourcesUnfollowed,
                                   IReadOnlyDictionary<int, Consumption> Delivered);

    /// <summary>How a dispatch consumes the value it is made on, over every instance of it.</summary>
    /// <param name="Unfollowed">Whether the receiver it reads may be an object the heap does not follow, as the engine's <c>Unfollowed</c>
    /// answers it from the receiver's unknown sources, source calls and completions.</param>
    /// <param name="Unresolved">Whether the heap marks the dispatch as one that may run a body it does not have.</param>
    private sealed record Consumption(bool Unfollowed, bool Unresolved);

    /// <summary>The run of the whole fixture.</summary>
    /// <param name="Heap">The solved heap.</param>
    /// <param name="Shared">The region of <c>Boxes.Shared</c>.</param>
    /// <param name="Other">The region of <c>Boxes.Other</c>.</param>
    private sealed record Matrix(HeapRun Heap, string Shared, string Other);

    private static Observed Observe(string name)
    {
        var heap = Results.Value.Heap.Heap;
        var prefix = $"{CELL_PREFIX}{name}.";
        var instances = heap.Instances.Values.Where(instance => instance.BodyId.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

        IEnumerable<SummaryVariable> Locals(string variable) =>
            instances.SelectMany(instance => instance.Summary.Variables.Where(value => value.SymbolKey.Contains($"|{variable}|", StringComparison.Ordinal)));

        IReadOnlyList<string> RegionsOf(string variable) =>
            instances.SelectMany(instance => instance.Summary.Variables.Where(value => value.SymbolKey.Contains($"|{variable}|", StringComparison.Ordinal))
                                                     .SelectMany(value => heap.Resolve(instance.Id, value.Values)))
                     .Distinct(StringComparer.Ordinal).ToArray();

        IReadOnlySet<string> RolesOf(string variable) => RegionsOf(variable).SelectMany(region => Role(region, prefix)).ToHashSet(StringComparer.Ordinal);

        // The dispatch on the consumed value stands in the action's own body; a fate's lambda dispatches on its parameter.
        var touches = instances.Where(instance => !instance.BodyId.Contains('#'))
                               .SelectMany(instance => instance.Summary.Calls.Where(call => call.Target.Contains("Box.Touch", StringComparison.Ordinal))
                                                               .Select(call => (instance.Id, call.OperationId)))
                               .ToArray();
        var runs = heap.Edges.Where(edge => heap.Instances[edge.CalleeInstance].BodyId.StartsWith(prefix, StringComparison.Ordinal) &&
                                            heap.Instances[edge.CalleeInstance].BodyId.Contains('#') &&
                                            heap.Instances[edge.CallerInstance].BodyId.StartsWith(prefix, StringComparison.Ordinal))
                             .Select(edge => heap.Instances[edge.CallerInstance].BodyId[prefix.Length..])
                             .ToHashSet(StringComparer.Ordinal);
        var ids = instances.Select(instance => instance.Id).ToHashSet(StringComparer.Ordinal);
        // The channels of the cell's own call; a `Take` reading a keeper delivers through a result of its own.
        var takes = instances.SelectMany(instance => instance.Summary.OpaqueCalls.Where(call => call.Callee.Contains("Lib.Take", StringComparison.Ordinal))
                                                                     .Select(call => (instance.Id, call.OperationId)))
                             .ToHashSet();
        var flagged = heap.UnfollowedModelCompletions.Where(item => ids.Contains(item.Instance) && !takes.Contains((item.Instance, item.Operation)))
                          .Select(item => item.Channel).ToHashSet(StringComparer.Ordinal);
        var sources = new[] { 1, 2 }.Where(source => Locals($"x{source}").Any(local => local.UnknownSources.Any(unknown =>
                                                         unknown is not (UnknownSource.Null or UnknownSource.FieldBeforeWrite or UnknownSource.SourceCall))))
                                    .ToHashSet();
        // The dispatch on each delivered gN, in the action's body or in the fate's lambda that reads it.
        var delivered = new Dictionary<int, Consumption>();
        foreach (var source in new[] { 1, 2 })
        {
            var dispatches = instances.SelectMany(instance => instance.Summary.Calls.Where(call => call.Target.Contains($"Box.Touch{source}", StringComparison.Ordinal))
                                                                   .Select(call => (Instance: instance, Call: call)))
                                      .ToArray();
            if (dispatches.Length == 0)
                continue;
            delivered[source] = new Consumption(
                dispatches.Any(item => item.Call.ReceiverUnknownSources.Any(unknown => unknown is not (UnknownSource.Null or UnknownSource.FieldBeforeWrite or UnknownSource.SourceCall)) ||
                                       item.Call.ReceiverSourceCalls.Any(call => heap.UnfollowedCallResults.Contains((item.Instance.Id, call))) ||
                                       item.Call.ReceiverCompletions.Any(completion => heap.UnfollowedCompletions.Contains((item.Instance.Id, completion)))),
                dispatches.Any(item => heap.UnresolvedCallTargets.Contains((item.Instance.Id, item.Call.OperationId))));
        }

        return new Observed(RolesOf("consumed"),
                            new[] { "g1", "g2" }.ToDictionary(variable => variable, RolesOf, StringComparer.Ordinal),
                            new[] { "t", "inner" }.Select(variable => new TaskLocal(variable, RegionsOf(variable)
                                                                                                 .Select(region => new TaskRegion(region, heap.Regions[region].Kind == HeapRegionKind.Task))
                                                                                                 .ToArray()))
                                                  .ToArray(),
                            touches.Any(heap.UnresolvedCallTargets.Contains), touches.Length, runs, flagged, sources, delivered);
    }

    /// <summary>The roles of a region a value holds: the shared and the other object by identity, an object created at the call of the
    /// class by its type with one level of what it holds, a library sequence with what it yields, a task region; anything else is
    /// foreign.</summary>
    /// <param name="regionId">The region.</param>
    /// <param name="prefix">The body id prefix of the action's class.</param>
    private static IEnumerable<string> Role(string regionId, string prefix)
    {
        var heap = Results.Value.Heap.Heap;
        if (regionId == Results.Value.Shared)
            return ["Shared"];
        if (regionId == Results.Value.Other)
            return ["Other"];
        var region = heap.Regions[regionId];
        if (region.Kind == HeapRegionKind.Task)
            return ["task"];
        IEnumerable<string> Contents() =>
            heap.PointsTo(regionId, PathValue.ELEMENT).Select(element => "element:" + Name(element))
                .Concat(heap.PointsTo(regionId, PathValue.KEYS).Select(key => "key:" + Name(key)));
        if (region.Display.StartsWith("sequence:", StringComparison.Ordinal) && region.SiteBodyId?.StartsWith(prefix, StringComparison.Ordinal) == true)
            return Contents().Prepend("sequence");
        // The array of xs the cell makes itself is no object created at the call.
        if (region.Kind == HeapRegionKind.Allocation && region.SiteBodyId?.StartsWith(prefix, StringComparison.Ordinal) == true &&
            region.TypeKey != "Fixture:Box[]")
            return Contents().Prepend("created:" + region.TypeKey);
        return [$"foreign:{region.Display}"];

        string Name(string element) => element == Results.Value.Shared ? "Shared" : element == Results.Value.Other ? "Other" : $"foreign:{heap.Regions[element].Display}";
    }

    // ---- the library ----

    /// <summary>The five members of a leaf: its direct form, the twin of depth 1, and its forms under one and two tasks.</summary>
    private static readonly string[] Forms = ["0", "T1", "V1", "T2", "V2"];

    private static string MemberName(Leaf leaf, string form) => $"{leaf}{form}";

    private static string FormOf(int depth, Outer outer) => (outer == Outer.Task ? "T" : "V") + depth;

    /// <summary>The type the leaf's member returns before any task stands around it.</summary>
    /// <param name="leaf">The leaf whose member's type is wanted.</param>
    private static string LeafType(Leaf leaf) => leaf switch
    {
        Leaf.Collection => "List<T>",
        Leaf.Dictionary => "Dictionary<T, T>",
        Leaf.Sequence => "IEnumerable<T>",
        _ => "T"
    };

    private static string Wrap(string type, string form) => form switch
    {
        "0" => type,
        "T1" => $"Task<{type}>",
        "V1" => $"ValueTask<{type}>",
        "T2" => $"Task<Task<{type}>>",
        "V2" => $"ValueTask<Task<{type}>>",
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, null)
    };

    private static string Parameters(Leaf leaf) => leaf switch
    {
        Leaf.New => "",
        Leaf.Argument => "T x",
        Leaf.Collection => "T[] xs",
        Leaf.Dictionary => "T k, T x",
        Leaf.Sequence => "T[] xs, Action<T> f",
        Leaf.Completion => "Task<T> t",
        _ => throw new ArgumentOutOfRangeException(nameof(leaf), leaf, null)
    };

    /// <summary>The leaf as a result form, and what the entry says besides.</summary>
    /// <param name="leaf">The leaf whose entry is written.</param>
    private static (string Result, string Besides) LeafForm(Leaf leaf) => leaf switch
    {
        Leaf.New => ("new", ""),
        Leaf.Argument => ("[arg:x]", ""),
        Leaf.Collection => ("collection(elements(arg:xs))", ""),
        Leaf.Dictionary => ("dictionary(arg:k,arg:x)", ""),
        Leaf.Sequence => ("sequence(elements(arg:xs))", ",\"fates\":{\"f\":{\"fate\":\"iterator\",\"inputs\":[[\"elements(arg:xs)\"]]}}"),
        Leaf.Completion => ("[completion(arg:t)]", ""),
        _ => throw new ArgumentOutOfRangeException(nameof(leaf), leaf, null)
    };

    private static string WrapResult(string result, string form) => form switch
    {
        "0" => result,
        "T1" or "V1" => $"task({result})",
        _ => $"task(task({result}))"
    };

    /// <summary>The parameters of a channel member by kind, in the order the member declares them: the sources, the fate's delegate, the
    /// keepers, the store targets, the outputs.</summary>
    /// <param name="uses">The member's channels.</param>
    /// <param name="task">Whether the sources are tasks (a cell) or values (a twin).</param>
    private static IReadOnlyList<(string Name, string Type)> ChannelParameters(IReadOnlyList<Use> uses, bool task)
    {
        var parameters = new List<(string Name, string Type)>();
        parameters.AddRange(uses.Select(use => use.Source).Order().Select(source => task ? ($"t{source}", "Task<T>") : ($"x{source}", "T")));
        var inputs = uses.Count(use => use.Channel == Channel.Input);
        if (inputs > 0)
            parameters.Add(("f", inputs == 1 ? "Action<T>" : "Action<T, T>"));
        parameters.AddRange(uses.Where(use => use.Channel == Channel.Keeps).Select(use => ($"k{use.Source}", "Keeper")));
        parameters.AddRange(uses.Where(use => use.Channel == Channel.Stores).Select(use => ($"a{use.Source}", "T[]")));
        parameters.AddRange(uses.Where(use => use.Channel == Channel.Outputs).Select(use => ($"o{use.Source}", "out T")));
        return parameters;
    }

    /// <summary>The channel's name as the heap marks it: the result, an output, a keeper or a store target by its parameter's ordinal, a
    /// fate input by its delegate's ordinal and its index.</summary>
    /// <param name="uses">The member's channels, which fix the parameters' ordinals.</param>
    /// <param name="use">The channel to name.</param>
    private static string ChannelName(IReadOnlyList<Use> uses, Use use)
    {
        var parameters = ChannelParameters(uses, task: true);
        int Ordinal(string name) => parameters.Select((parameter, ordinal) => (parameter.Name, ordinal)).Single(item => item.Name == name).ordinal;
        return use.Channel switch
        {
            Channel.Result => "result",
            Channel.Input => $"fate{Ordinal("f")}.{uses.Where(other => other.Channel == Channel.Input).ToList().IndexOf(use)}",
            Channel.Keeps => $"keep{Ordinal($"k{use.Source}")}",
            Channel.Stores => $"store{Ordinal($"a{use.Source}")}",
            Channel.Outputs => $"output{Ordinal($"o{use.Source}")}",
            _ => throw new ArgumentOutOfRangeException(nameof(use), use, null)
        };
    }

    private static string ChannelMember(ChannelCell cell, bool task) => (task ? "" : "TW") + cell.Name;

    private static (string Declaration, string Entry) ChannelDeclaration(ChannelCell cell, bool task)
    {
        string Value(int source) => task ? $"completion(arg:t{source})" : $"arg:x{source}";
        var uses = cell.Uses;
        var result = uses.SingleOrDefault(use => use.Channel == Channel.Result);
        var parameters = ChannelParameters(uses, task);
        var declaration = $"public static {(result is null ? "void" : cell.Synchronous ? "T" : "Task<T>")} {ChannelMember(cell, task)}<T>(" +
                          string.Join(", ", parameters.Select(parameter => $"{parameter.Type} {parameter.Name}")) + ") { " +
                          string.Join(" ", parameters.Where(parameter => parameter.Type == "out T").Select(parameter => $"{parameter.Name} = default!;")) +
                          (result is null ? "" : " return default!;") + " }";
        var parts = new List<string>();
        var stores = uses.Where(use => use.Channel == Channel.Stores).ToArray();
        parts.Add("\"effects\":{" + string.Join(",", stores.Select(use => $"\"a{use.Source}\":[\"writes-cells\"]")) + "}");
        if (result is not null)
            parts.Add(cell.Synchronous ? $"\"result\":\"[{Value(result.Source)}]\"" : $"\"result\":\"task([{Value(result.Source)}])\"");
        var inputs = uses.Where(use => use.Channel == Channel.Input).ToArray();
        if (inputs.Length > 0)
            parts.Add("\"fates\":{\"f\":{\"fate\":\"invoke-now\",\"inputs\":[" + string.Join(",", inputs.Select(use => $"[\"{Value(use.Source)}\"]")) + "]}}");
        var keeps = uses.Where(use => use.Channel == Channel.Keeps).ToArray();
        if (keeps.Length > 0)
            parts.Add("\"keeps\":{" + string.Join(",", keeps.Select(use => $"\"k{use.Source}\":[\"{Value(use.Source)}\"]")) + "}");
        if (stores.Length > 0)
            parts.Add("\"stores\":{" + string.Join(",", stores.Select(use => $"\"a{use.Source}\":[\"{Value(use.Source)}\"]")) + "}");
        var outputs = uses.Where(use => use.Channel == Channel.Outputs).ToArray();
        if (outputs.Length > 0)
            parts.Add("\"outputs\":{" + string.Join(",", outputs.Select(use => $"\"o{use.Source}\":\"[{Value(use.Source)}]\"")) + "}");
        return (declaration, string.Join(",", parts));
    }

    private static IEnumerable<ChannelCell> AllChannelCells() => ChannelCells().Concat(IsolationCells()).Concat(SynchronousCells());

    private static (string Source, IReadOnlyList<(string Member, string Decision)> Entries) Library()
    {
        var text = new StringBuilder("""
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;

            namespace TaskLib
            {
                public sealed class Keeper { }

                public static class Lib
                {
                    public static object Take(Keeper k) => null!;

            """);
        var entries = new List<(string Member, string Decision)> { ("Take", "\"result\":\"[kept:k]\"") };
        foreach (var leaf in Enum.GetValues<Leaf>())
        foreach (var form in Forms)
        {
            var (result, besides) = LeafForm(leaf);
            text.Append($"        public static {Wrap(LeafType(leaf), form)} {MemberName(leaf, form)}<T>({Parameters(leaf)}) => default!;\n");
            entries.Add((MemberName(leaf, form), $"\"result\":\"{WrapResult(result, form)}\"{besides}"));
        }

        foreach (var cell in AllChannelCells())
        foreach (var task in new[] { true, false })
        {
            var (declaration, entry) = ChannelDeclaration(cell, task);
            text.Append($"        {declaration}\n");
            entries.Add((ChannelMember(cell, task), entry));
        }

        text.Append("    }\n}\n");
        return (text.ToString(), entries);
    }

    // ---- the fixture ----

    private static string TwinName(ResultCell cell) => $"TW_{cell.Leaf}_{(cell.Depth == 1 ? "0" : "T1")}_{(cell.Depth == 1 ? "None" : cell.Consumer.ToString())}";

    private static IEnumerable<string> Actions() =>
        ResultCells().Select(cell => cell.Name).Concat(ResultCells().Select(TwinName).Distinct())
                     .Concat(AllChannelCells().SelectMany(cell => new[] { cell.Name, cell.TwinName }));

    private static Matrix Run()
    {
        var (librarySource, entries) = Library();
        var library = TaskModelFixture.Library(librarySource);
        var heap = TaskModelFixture.Solve(Source(), library, entries.Select(entry => (TaskModelFixture.Id(library.Compilation, "Lib", entry.Member), entry.Decision)));
        var statics = heap.Heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Static && region.TypeKey == "Fixture:Boxes").ToArray();
        string Of(string field) => Assert.Single(statics.SelectMany(region => heap.Heap.PointsTo(region.Identity, field)).Distinct(StringComparer.Ordinal));
        return new Matrix(heap, Of("Shared"), Of("Other"));
    }

    private static string Source()
    {
        var text = new StringBuilder("""
            public class Box
            {
                public int Count;
                public virtual void Touch() { }
                public virtual void Touch1() { }
                public virtual void Touch2() { }
            }

            public static class Boxes
            {
                public static readonly Box Shared = new Box();
                public static readonly Box Other = new Box();
            }

            public static class Externals
            {
                public static extern Box MakeBox();
            }

            """);
        foreach (var cell in ResultCells())
            text.Append(Class(cell.Name, ResultBody(cell.Leaf, FormOf(cell.Depth, cell.Outer), cell.Depth, cell.Consumer)));
        foreach (var twin in ResultCells().GroupBy(TwinName))
        {
            var cell = twin.First();
            text.Append(Class(twin.Key, cell.Depth == 1 ? ResultBody(cell.Leaf, "0", 0, cell.Consumer) : ResultBody(cell.Leaf, "T1", 1, cell.Consumer)));
        }

        foreach (var cell in AllChannelCells())
        {
            text.Append(Class(cell.Name, ChannelBody(cell, task: true)));
            text.Append(Class(cell.TwinName, ChannelBody(cell, task: false)));
        }

        text.Append("public sealed class MatrixController : ControllerBase\n{\n");
        foreach (var action in Actions())
            text.Append($"    public async Task {action}() => await new Cell_{action}().Run();\n");
        text.Append("}\n");
        return text.ToString();
    }

    private static string Class(string name, string body) => $"public sealed class Cell_{name}\n{{\npublic async Task Run()\n{{\n{body}}}\n}}\n\n";

    /// <summary>The body of a result cell or twin: the call of the leaf's member in <paramref name="form"/>, consumed
    /// <paramref name="consumptions"/> times, and the use of what that gives.</summary>
    /// <param name="leaf">The leaf whose member is called, which fixes the arguments and the use.</param>
    /// <param name="form">The form of the leaf's member to call.</param>
    /// <param name="consumptions">How many times the call's task is consumed: 0, 1 or 2.</param>
    /// <param name="consumer">How each consumption reads the task: an await or <c>.Result</c>.</param>
    private static string ResultBody(Leaf leaf, string form, int consumptions, Consumer consumer)
    {
        var (prelude, arguments) = leaf switch
        {
            Leaf.New => ("", ""),
            Leaf.Argument => ("", "Boxes.Shared"),
            Leaf.Collection => ("var xs = new Box[] { Boxes.Shared, Boxes.Other };\n", "xs"),
            Leaf.Dictionary => ("", "Boxes.Other, Boxes.Shared"),
            Leaf.Sequence => ("var xs = new Box[] { Boxes.Shared, Boxes.Other };\n", "xs, item => { var seen = item; }"),
            Leaf.Completion => ("var source = Task.FromResult(Boxes.Shared);\n", "source"),
            _ => throw new ArgumentOutOfRangeException(nameof(leaf), leaf, null)
        };
        string Consume(string task) => consumer == Consumer.Await ? $"await {task}" : $"{task}.Result";
        var call = $"TaskLib.Lib.{MemberName(leaf, form)}<Box>({arguments})";
        var body = new StringBuilder(prelude);
        switch (consumptions)
        {
            case 0:
                body.Append($"var consumed = {call};\n");
                break;
            case 1:
                body.Append($"var t = {call};\nvar consumed = {Consume("t")};\n");
                break;
            default:
                body.Append($"var t = {call};\nvar inner = {Consume("t")};\nvar consumed = {Consume("inner")};\n");
                break;
        }

        body.Append(leaf switch
        {
            Leaf.Collection => "consumed[0].Touch();\n",
            Leaf.Dictionary => "consumed[Boxes.Other].Touch();\n",
            Leaf.Sequence => "foreach (var item in consumed)\n    item.Touch();\n",
            _ => "consumed.Touch();\n"
        });
        return body.ToString();
    }

    private static string Value(Completion completion) => completion switch
    {
        Completion.Known => "Boxes.Shared",
        Completion.Opaque => "Externals.MakeBox()",
        Completion.Mixed => $"({FLAG} ? Boxes.Shared : Externals.MakeBox())",
        _ => throw new ArgumentOutOfRangeException(nameof(completion), completion, null)
    };

    /// <summary>The body of a channel or isolation cell or twin: each source made, the call, and each channel's value read into
    /// <c>gN</c> for source N and dispatched on.</summary>
    /// <param name="cell">The cell whose sources, channels and completions the body builds.</param>
    /// <param name="task">Whether the sources are tasks (the cell) or values (its twin).</param>
    private static string ChannelBody(ChannelCell cell, bool task)
    {
        var body = new StringBuilder();
        foreach (var source in cell.Uses.Select(use => use.Source).Order())
            body.Append(task ? $"var t{source} = Task.FromResult<Box>({Value(cell.Completions[source - 1])});\n"
                             : $"Box x{source} = {Value(cell.Completions[source - 1])};\n");
        foreach (var use in cell.Uses.Where(use => use.Channel == Channel.Keeps))
            body.Append($"var k{use.Source} = new TaskLib.Keeper();\n");
        foreach (var use in cell.Uses.Where(use => use.Channel == Channel.Stores))
            body.Append($"var a{use.Source} = new Box[1];\n");
        var inputs = cell.Uses.Where(use => use.Channel == Channel.Input).ToArray();
        var arguments = ChannelParameters(cell.Uses, task).Select(parameter => parameter.Name switch
        {
            "f" => inputs.Length == 1
                ? $"p0 => {{ var g{inputs[0].Source} = p0; g{inputs[0].Source}.Touch(); g{inputs[0].Source}.Touch{inputs[0].Source}(); }}"
                : $"(p0, p1) => {{ var g{inputs[0].Source} = p0; var g{inputs[1].Source} = p1; " +
                  $"g{inputs[0].Source}.Touch{inputs[0].Source}(); g{inputs[1].Source}.Touch{inputs[1].Source}(); }}",
            var name when name.StartsWith('o') => $"out var {name}",
            var name => name
        });
        var call = $"TaskLib.Lib.{ChannelMember(cell, task)}<Box>({string.Join(", ", arguments)})";
        var result = cell.Uses.SingleOrDefault(use => use.Channel == Channel.Result);
        body.Append(result is null ? $"{call};\n"
                    : $"var g{result.Source} = {(cell.Synchronous ? "" : "await ")}{call};\ng{result.Source}.Touch();\ng{result.Source}.Touch{result.Source}();\n");
        foreach (var use in cell.Uses)
        {
            body.Append(use.Channel switch
            {
                Channel.Keeps => $"var g{use.Source} = (Box)TaskLib.Lib.Take(k{use.Source});\ng{use.Source}.Touch{use.Source}();\n",
                Channel.Stores => $"var g{use.Source} = a{use.Source}[0];\ng{use.Source}.Touch{use.Source}();\n",
                Channel.Outputs => $"var g{use.Source} = o{use.Source};\ng{use.Source}.Touch{use.Source}();\n",
                _ => ""
            });
        }

        return body.ToString();
    }
}
