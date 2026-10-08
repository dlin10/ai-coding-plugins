using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;

namespace ConcurrencyHunter.Heap;

/// <summary>Builds a body's summary the first time it is asked for, and counts how many it built.</summary>
/// <param name="bodies">The lowered bodies by id, which summaries are built from.</param>
/// <param name="program">The program index the summaries are built against.</param>
/// <param name="limits">The limits the summaries are built under.</param>
public sealed class SummaryCache(IReadOnlyDictionary<string, IrBody> bodies, ProgramIndex program, AnalysisLimits limits)
{
    private readonly Dictionary<string, MethodSummary> _summaries = new(StringComparer.Ordinal);

    public int Built => _summaries.Count;

    /// <summary>The limits the summaries are built under, which the accesses expanded from them keep too.</summary>
    public AnalysisLimits Limits => limits;

    public bool IsBuilt(string bodyId) => _summaries.ContainsKey(bodyId);

    public MethodSummary? Get(string bodyId)
    {
        if (_summaries.TryGetValue(bodyId, out var summary))
            return summary;
        if (!bodies.TryGetValue(bodyId, out var body))
            return null;
        summary = MethodSummaryBuilder.Build(body, program, limits, bodies.GetValueOrDefault);
        _summaries.Add(bodyId, summary);
        return summary;
    }
}

/// <summary>Everything the whole-program fixpoint of one process scope is solved over.</summary>
/// <param name="ScopeId">The id of the process scope.</param>
/// <param name="Roots">The execution roots of the scope.</param>
/// <param name="Reachable">The bodies reachable from the roots.</param>
/// <param name="Summaries">The cache of the bodies' summaries.</param>
/// <param name="Program">The program index.</param>
/// <param name="DiIndex">The DI registrations of the scope.</param>
/// <param name="InjectionBindings">The constructor and member injections of the types.</param>
public sealed record ScopeProgram(string ScopeId, IReadOnlyList<ExecutionRootDescriptor> Roots, ReachableSetResult Reachable,
                                  SummaryCache Summaries, ProgramIndex Program, DiIndex DiIndex,
                                  IReadOnlyList<TypeInjectionBindings> InjectionBindings)
{
    public IReadOnlyDictionary<string, IReadOnlySet<string>> MetadataSupertypes { get; init; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
}

public enum HeapRegionKind
{
    Static,
    Di,
    Allocation,
    Receiver,
    Delegate,
    Container,
    Provider,
    Symbolic,
    Task
}

/// <summary>One object or set of objects. <see cref="Identity"/> includes the context; <see cref="Display"/> never does.
/// An open region names a type parameter and stands for every closed region of its <see cref="Group"/>.
/// <see cref="MayOverlapItself"/> marks a hosted service whose instance count is unknown. <see cref="SiteBodyId"/> and
/// <see cref="SiteOperationId"/> name an allocation's or a delegate creation's site.</summary>
/// <param name="Identity">The region's identity including its context.</param>
/// <param name="Kind">The source of the region in the heap.</param>
/// <param name="Display">The context-free name shown in reports.</param>
/// <param name="TypeKey">The assembly-aware type of the region, when known.</param>
/// <param name="Context">The creation context.</param>
/// <param name="Group">The allocation group relating open and closed regions.</param>
/// <param name="IsOpen">Whether the region stands for a type parameter's constructions.</param>
/// <param name="IsMerged">Whether the region combines bounded contexts.</param>
/// <param name="MayOverlapItself">Whether independent executions may reach the same hosted service.</param>
/// <param name="SiteBodyId">The body containing the creation site, when present.</param>
/// <param name="SiteOperationId">The operation at that creation site, when present.</param>
public sealed record HeapRegion(string Identity, HeapRegionKind Kind, string Display, string? TypeKey, string Context, string Group,
                                bool IsOpen, bool IsMerged, bool MayOverlapItself = false, string? SiteBodyId = null, int? SiteOperationId = null)
{
    public bool HasExactType { get; init; }
    /// <summary>The context-free identity of an object a model creates for a destination and path, when it uses that vocabulary.</summary>
    public string? ModelCreationKey { get; init; }
}

/// <summary>An instantiation of a body: its context, type substitution and the regions its receiver and parameters point to.
/// A receiverless instance has no <c>this</c>; its accesses based on <c>this</c> have no resource.</summary>
/// <param name="Id">The instance's id.</param>
/// <param name="BodyId">The body it instantiates.</param>
/// <param name="Context">Its context.</param>
/// <param name="Substitution">The type substitution, by type parameter.</param>
/// <param name="IsMerged">Whether it is the body's merged context.</param>
/// <param name="IsReceiverless">Whether it has no <c>this</c>.</param>
/// <param name="Summary">The body's summary.</param>
/// <param name="Receivers">The regions its receiver points to.</param>
/// <param name="Parameters">The regions each parameter points to, by ordinal.</param>
/// <param name="CellOwners">The instances whose capture cells hold the variables its body captures.</param>
public sealed record MethodInstance(string Id, string BodyId, string Context, IReadOnlyDictionary<string, string> Substitution,
                                    bool IsMerged, bool IsReceiverless, MethodSummary Summary, IReadOnlySet<string> Receivers,
                                    IReadOnlyDictionary<int, IReadOnlySet<string>> Parameters, IReadOnlySet<string> CellOwners);

public sealed record CallEdge(string CallerInstance, int OperationId, string CalleeInstance, string Reason);

public sealed record IteratorObject(string RegionId, CallEdge Creation);

/// <summary>A closed type's type initializer: the instance running it and what triggered it.</summary>
/// <param name="TypeKey">The closed type.</param>
/// <param name="InstanceId">The instance running its type initializer.</param>
/// <param name="TriggeringInstances">The instances that triggered it.</param>
/// <param name="TriggeringRegions">The regions that triggered it.</param>
public sealed record TypeInitializerConstruction(string TypeKey, string InstanceId, IReadOnlyList<string> TriggeringInstances,
                                                 IReadOnlyList<string> TriggeringRegions);

/// <summary>A region the container or the framework constructs, with the constructor instances that run for it.</summary>
/// <param name="RegionId">The constructed region.</param>
/// <param name="ConstructorInstances">The constructor instances that run for it.</param>
public sealed record RegionConstruction(string RegionId, IReadOnlyList<string> ConstructorInstances);

/// <summary>An operation of an instance that triggers the construction of a region: a locator call, or the entry (operation
/// <c>-1</c>) of a root or constructor body whose injections resolve it.</summary>
/// <param name="InstanceId">The triggering instance.</param>
/// <param name="OperationId">The triggering operation, or <c>-1</c> for the instance's entry.</param>
/// <param name="RegionId">The region whose construction it triggers.</param>
public sealed record RegionTrigger(string InstanceId, int OperationId, string RegionId);

public enum SpawnRole
{
    Work,
    LocalInit,
    LocalFinally
}

public sealed record SpawnCallee(string InstanceId, SpawnRole Role);

/// <summary>A spawn of a caller instance (<see cref="SummarySpawn"/>): the handle regions its site creates in the caller's context,
/// or the started threads; the tail region when the handle's work returns a task the handle does not wait for; the instances it
/// runs. These are not call edges: the spawned bodies run as their own work.</summary>
/// <param name="CallerInstance">The instance making the spawn.</param>
/// <param name="OperationId">The spawn operation.</param>
/// <param name="CallOperationId">The call operation the spawn lowers.</param>
/// <param name="Kind">The kind of spawn.</param>
/// <param name="Handles">The handle regions, or the started threads.</param>
/// <param name="Tail">The tail region, when the handle's work returns a task the handle does not wait for.</param>
/// <param name="Callees">The instances it runs, with their roles.</param>
public sealed record SpawnSite(string CallerInstance, int OperationId, int CallOperationId, IrSpawnKind Kind, IReadOnlySet<string> Handles,
                               string? Tail, IReadOnlyList<SpawnCallee> Callees);

/// <summary>A timer's callback or <c>Elapsed</c> handler (<see cref="SummaryTimer"/>) with the timer regions and the instances it runs.</summary>
/// <param name="CallerInstance">The instance making the timer operation.</param>
/// <param name="OperationId">The timer operation.</param>
/// <param name="Action">What the timer operation does.</param>
/// <param name="Timers">The timer regions.</param>
/// <param name="Callees">The instances the callback or handler runs.</param>
public sealed record TimerCallbackSite(string CallerInstance, int OperationId, IrTimerAction Action, IReadOnlySet<string> Timers,
                                       IReadOnlyList<string> Callees)
{
    /// <summary>Whether <see cref="Timers"/> names every timer the site may run: an unknown origin may be any timer, whatever the timers
    /// beside it are.</summary>
    public bool TimersKnown { get; init; } = true;
}

/// <summary>A resolved call edge into an async body whose result is not awaited at once: an <see cref="IrSpawnKind.AsyncCall"/>,
/// whose result is the <see cref="Handle"/> region of the site in the caller's context, or an <see cref="IrSpawnKind.AsyncVoid"/>.
/// The edges themselves stay call edges: the body runs in the caller up to its first await.</summary>
/// <param name="CallerInstance">The instance making the call.</param>
/// <param name="OperationId">The spawn operation of the call.</param>
/// <param name="Kind">The kind of spawn: an async call or an async void.</param>
/// <param name="Handle">The handle region of the site, for an async call.</param>
/// <param name="Callees">The async instances the call runs.</param>
public sealed record AsyncSpawnSite(string CallerInstance, int OperationId, IrSpawnKind Kind, string? Handle, IReadOnlyList<string> Callees);

/// <summary>A delegate region handed to unresolved calls (R3): the calls that handed it, by caller instance and operation, and the
/// instances running its target. These are not call edges: the body runs in an unknown execution of its own.</summary>
/// <param name="RegionId">The delegate region.</param>
/// <param name="Sites">The calls that handed it, by caller instance and operation.</param>
/// <param name="Callees">The instances running its target.</param>
public sealed record DelegateHandoff(string RegionId, IReadOnlyList<(string CallerInstance, int OperationId)> Sites, IReadOnlyList<string> Callees);

/// <summary>A library sequence a known call returned, or one a value of its model created, or a grouping (R5). The call that created it,
/// its deferred effects being that call's in its summary where it is the call's result; the sources its enumeration enumerates; what it
/// yields and, for a grouping, its key; and the instances an unknown enumeration of it runs.</summary>
/// <param name="RegionId">The sequence's region.</param>
/// <param name="CreatorInstance">The instance making the call that created it; empty for a grouping.</param>
/// <param name="CreatorOperation">That call's operation.</param>
/// <param name="IsResult">Whether it is the call's result rather than a sequence a value of its model created.</param>
/// <param name="IsGrouping">Whether it is a grouping.</param>
/// <param name="Sources">The sources its enumeration enumerates.</param>
/// <param name="Yields">What it yields.</param>
/// <param name="Keys">A grouping's key.</param>
/// <param name="EnumerationInstances">The instances an unknown enumeration of it runs.</param>
public sealed record LibrarySequence(string RegionId, string CreatorInstance, int CreatorOperation, bool IsResult, bool IsGrouping,
                                     IReadOnlyList<string> Sources, IReadOnlyList<string> Yields, IReadOnlyList<string> Keys,
                                     IReadOnlyList<string> EnumerationInstances)
{
    /// <summary>The sources that are what a delegate of the call returned, among <see cref="Sources"/>: no argument effect of the call's
    /// summary names them, so its enumeration enumerates them itself.</summary>
    public IReadOnlyList<string> ReturnedSources { get; init; } = [];

    /// <summary>The iterator delegates no body the run has resolves: an unresolved dispatch wherever the sequence is enumerated, its
    /// unknown enumeration included (R3).</summary>
    public IReadOnlyList<SequenceDispatch> Unresolved { get; init; } = [];
}

/// <summary>An unresolved iterator delegate of a library sequence: the callee, the objects its inputs hand it and the receivers its
/// captures are seen through.</summary>
/// <param name="Callee">The unresolved iterator delegate.</param>
/// <param name="Inputs">The objects its inputs hand it.</param>
/// <param name="Receivers">The receivers its captures are seen through, each with the run's own declaring type, when any.</param>
public sealed record SequenceDispatch(string Callee, IReadOnlyList<string> Inputs, IReadOnlyList<(string Region, string? DeclaringTypeKey)> Receivers);

/// <summary>A delegate region a known call runs by its <c>startup</c> fate, the call by caller instance and operation, and one instance
/// running its target.</summary>
/// <param name="CallerInstance">The instance making the known call.</param>
/// <param name="OperationId">The known call's operation.</param>
/// <param name="RegionId">The delegate region.</param>
/// <param name="CalleeInstance">An instance running its target.</param>
public sealed record StartupDelegate(string CallerInstance, int OperationId, string RegionId, string CalleeInstance);

/// <summary>The tasks a <c>Task.WhenAll</c> result completes after, or that they are unknown.</summary>
/// <param name="Members">The task regions it completes after.</param>
/// <param name="MembersKnown">Whether <paramref name="Members"/> names every task it completes after.</param>
public sealed record TaskGroup(IReadOnlySet<string> Members, bool MembersKnown);

/// <summary>What completes a task region, as <see cref="TaskCompleter"/> says where to read it.</summary>
public enum TaskCompleterKind
{
    /// <summary>What the body of <see cref="TaskCompleter.Instance"/> returns: an async body's returns, or a work's.</summary>
    Returns,

    /// <summary>What the tasks the body of <see cref="TaskCompleter.Instance"/> returns complete with: a non-async work whose task the
    /// spawn waits for.</summary>
    ReturnedTasks,

    /// <summary>The work values the spawn <see cref="TaskCompleter.Operation"/> of the instance was handed; the instances they run
    /// complete it as well.</summary>
    Spawn,

    /// <summary>The task operation <see cref="TaskCompleter.Operation"/> of the instance (<see cref="SummaryTaskOperation"/>).</summary>
    TaskOperation,

    /// <summary>The new array of the <c>WhenAll</c> <see cref="TaskCompleter.Operation"/> of the instance, which holds the completion
    /// values of its tasks.</summary>
    WhenAll,

    /// <summary>The result form of the model of the known call <see cref="TaskCompleter.Operation"/> of the instance.</summary>
    ModelResult,

    /// <summary>A task region the heap makes at the operation: a tail, a further level of a model's <c>task(…)</c>.</summary>
    Task,

    /// <summary>Code the analysis does not see in full: a work no body is resolved for, a dispatch not every body of which is known.</summary>
    Unseen
}

/// <summary>One thing that completed a task region: where its completion value comes from.</summary>
/// <param name="Instance">The instance whose code completes the task: the callee whose returns do, or the instance making the
/// operation.</param>
/// <param name="Operation">The operation in that instance, or <c>-1</c> for a callee's returns.</param>
/// <param name="Kind">How to read what completes it.</param>
public sealed record TaskCompleter(string Instance, int Operation, TaskCompleterKind Kind);

/// <summary>A delegate parameter of a known call: the instance making the call, the call and the parameter.</summary>
/// <param name="Instance">The instance making the known call.</param>
/// <param name="Operation">The known call's operation.</param>
/// <param name="Ordinal">The delegate parameter's ordinal.</param>
public sealed record DelegateSite(string Instance, int Operation, int Ordinal);

/// <summary>One run of a delegate at a known call: the instance it runs, and the task region the run gives back where that body is
/// async and not void, whose completion is what the body returns.</summary>
/// <param name="Callee">The instance the delegate runs.</param>
/// <param name="Task">The task region the run gives back, or null where the run gives back what the body returns.</param>
public sealed record DelegateRun(string Callee, string? Task);

/// <summary>What one run of a delegate a model may name the returns of gives back at a known call, as the engine answers
/// <c>returns:</c> (R1, R2, R4).</summary>
/// <param name="Runs">The delegate's runs.</param>
/// <param name="Regions">The objects the runs give back: each async run's task region, every other run's returns.</param>
/// <param name="Unseen">Whether some alternative of the delegate runs code the analysis does not see in full: no delegate object is
/// known for it, one resolves no body, a dispatch of one has no receiver, or the argument may be an object the heap does not follow.</param>
public sealed record DelegateReturnRuns(IReadOnlyList<DelegateRun> Runs, IReadOnlySet<string> Regions, bool Unseen);

public static class HeapCounters
{
    public const string MERGED_CONTEXT = "merged-context";
    public const string SCC_BUDGET_EXCEEDED = "scc-budget-exceeded";
    public const string NO_RECEIVER_OBJECT = "no-receiver-object";
    public const string REACHABLE_BODIES = "reachable-bodies";
    public const string LOWERED_NOT_REACHED = "lowered-not-reached";
    public const string UNRESOLVED_LOCATOR = "unresolved-locator";
    public const string PROPAGATE_PASSES = "propagate-passes";
    public const string INSTANCE_PROCESSINGS = "instance-processings";
    public const string REFERENCE_LOOKUPS = "reference-lookups";
}

/// <summary>The solved heap of one scope.</summary>
public sealed class HeapSolution
{
    private readonly Func<string, AbstractValue, IReadOnlySet<string>> _resolve;
    private readonly Func<string, string, IReadOnlySet<string>> _load;
    private readonly Func<string, string, IReadOnlySet<string>> _cell;
    private readonly Func<string, IrFieldRef, string> _staticRegion;
    private readonly Func<string, IReadOnlyList<string>> _fieldsOf;
    private readonly Func<string, IReadOnlySet<string>> _delegateCaptures;

    internal HeapSolution(IReadOnlyDictionary<string, HeapRegion> regions, IReadOnlyDictionary<string, MethodInstance> instances,
                          IReadOnlyList<CallEdge> edges, IReadOnlyList<TypeInitializerConstruction> typeInitializers,
                          IReadOnlyList<RegionConstruction> constructions, IReadOnlyDictionary<string, int> counters,
                          IReadOnlySet<string> reachableBodies, IReadOnlyList<string> loweredNotReached,
                          IReadOnlyList<(string BodyId, int OperationId)> noReceiverObjects,
                          Func<string, AbstractValue, IReadOnlySet<string>> resolve, Func<string, string, IReadOnlySet<string>> load,
                          Func<string, string, IReadOnlySet<string>> cell, IReadOnlyDictionary<string, string> rootInstances,
                          Func<string, IrFieldRef, string> staticRegion, Func<string, IReadOnlyList<string>> fieldsOf,
                          Func<string, IReadOnlySet<string>> delegateCaptures)
    {
        _cell = cell;
        RootInstances = rootInstances;
        _staticRegion = staticRegion;
        _fieldsOf = fieldsOf;
        _delegateCaptures = delegateCaptures;
        Regions = regions;
        Instances = instances;
        Edges = edges;
        TypeInitializers = typeInitializers;
        Constructions = constructions;
        Counters = counters;
        ReachableBodies = reachableBodies;
        LoweredNotReached = loweredNotReached;
        NoReceiverObjects = noReceiverObjects;
        _resolve = resolve;
        _load = load;
    }

    /// <summary>The regions of the solved heap, in the order the solve made them. A lookup by id also finds a region a query named after
    /// the solve, an object the solve never evaluated: it holds nothing, no region points to it, and no enumeration lists it, so every
    /// stage after the solve sees the same heap (ADR 0016).</summary>
    public IReadOnlyDictionary<string, HeapRegion> Regions { get; }
    public IReadOnlyDictionary<string, MethodInstance> Instances { get; }
    public IReadOnlyList<CallEdge> Edges { get; }
    public IReadOnlyList<CallEdge> ExecutionEdges { get; init; } = [];
    public IReadOnlyList<IteratorObject> IteratorObjects { get; init; } = [];
    public IReadOnlySet<string> UnknownIterators { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Iterator receivers handled at enumeration members, by caller instance and call operation.</summary>
    public IReadOnlyDictionary<(string Instance, int Operation), IReadOnlySet<string>> IteratorMemberReceivers { get; init; } =
        new Dictionary<(string, int), IReadOnlySet<string>>();
    public IReadOnlyList<TypeInitializerConstruction> TypeInitializers { get; }
    public IReadOnlyList<RegionConstruction> Constructions { get; }
    public IReadOnlyDictionary<string, int> Counters { get; }
    public IReadOnlySet<string> ReachableBodies { get; }
    public IReadOnlyList<string> LoweredNotReached { get; }
    public IReadOnlyList<(string BodyId, int OperationId)> NoReceiverObjects { get; }

    /// <summary>The call results whose value may come from an origin points-to does not follow: the callee may return an object the heap
    /// cannot name, so the regions of the result are not all the result may be.</summary>
    public IReadOnlySet<(string Instance, int Operation)> UnfollowedCallResults { get; init; } = new HashSet<(string, int)>();

    /// <summary>What completed each task region (<see cref="TaskCompleter"/>): the instances and operations its completion value comes
    /// from. A task region with none here has a producer the heap does not name.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<TaskCompleter>> TaskCompleters { get; init; } =
        new Dictionary<string, IReadOnlySet<TaskCompleter>>(StringComparer.Ordinal);

    /// <summary>What one run of each <c>invoke-now</c> or <c>iterator</c> delegate of a known call gives back, by the delegate
    /// parameter: the one answer the engine gives a model's <c>returns:</c>. A site with none here has no answer the engine gave.</summary>
    public IReadOnlyDictionary<DelegateSite, DelegateReturnRuns> DelegateReturns { get; init; } = new Dictionary<DelegateSite, DelegateReturnRuns>();

    /// <summary>The delegate parameters of known calls whose fate runs each instance, by the instance: its parameters are handed the
    /// fate's inputs there.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<DelegateSite>> FateRuns { get; init; } =
        new Dictionary<string, IReadOnlySet<DelegateSite>>(StringComparer.Ordinal);

    /// <summary>The awaits and <c>Unwrap()</c> calls whose value may come from such an origin: a task they read may complete with an object
    /// the heap cannot name (<see cref="SummaryValue.Completions"/>).</summary>
    public IReadOnlySet<(string Instance, int Operation)> UnfollowedCompletions { get; init; } = new HashSet<(string, int)>();

    /// <summary>The channels of known calls through which a <c>completion(…)</c> value of their model may deliver an object the heap
    /// does not follow, each answering for itself: the result, an output, a fate input, a keeper or a store target
    /// (<see cref="WholeProgram"/>'s channel names). A task it names may complete with such an object, or be one the heap does not
    /// follow.</summary>
    public IReadOnlySet<(string Instance, int Operation, string Channel)> UnfollowedModelCompletions { get; init; } =
        new HashSet<(string, int, string)>();

    /// <summary>What a task region completes with: the regions its completion slot holds.</summary>
    /// <param name="task">The task region.</param>
    public IReadOnlySet<string> Completion(string task) => CompletionQuery(task);

    internal Func<string, IReadOnlySet<string>> CompletionQuery { get; init; } = _ => new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The task regions whose completion may be an object the heap does not follow: what completes them may be one, or their
    /// producer is code the analysis does not see in full.</summary>
    public IReadOnlySet<string> UnfollowedTaskCompletions { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>What a call gives a value that consumed the call's task <paramref name="depth"/> times
    /// (<see cref="SummaryValue.Depths(int)"/>): the call's result, or what its tasks complete with, followed that many times.</summary>
    /// <param name="instance">The instance making the call.</param>
    /// <param name="call">The call's operation.</param>
    /// <param name="depth">How many times the value consumed the call's task.</param>
    public IReadOnlySet<string> Gives(string instance, int call, int depth) => Completions(Resolve(instance, new CallResultValue(call)), depth, Completion);

    /// <summary>What tasks complete with, followed <paramref name="depth"/> times: the tasks themselves for none. Nothing for
    /// <see cref="SummaryValue.MAX_COMPLETION_DEPTH"/>, which stands for any deeper consumption as well.</summary>
    /// <param name="tasks">The regions to start from.</param>
    /// <param name="depth">How many completions to follow.</param>
    /// <param name="completion">What one task region completes with.</param>
    public static IReadOnlySet<string> Completions(IEnumerable<string> tasks, int depth, Func<string, IEnumerable<string>> completion)
    {
        var regions = depth >= SummaryValue.MAX_COMPLETION_DEPTH ? new HashSet<string>(StringComparer.Ordinal) : tasks.ToHashSet(StringComparer.Ordinal);
        for (var level = 0; level < depth && regions.Count != 0; level++)
            regions = regions.SelectMany(completion).ToHashSet(StringComparer.Ordinal);
        return regions;
    }

    /// <summary>The virtual, interface and delegate calls whose receiver may come from such an origin: the edges of the call name the
    /// bodies the heap could resolve, not every body the call may run.</summary>
    public IReadOnlySet<(string Instance, int Operation)> UnresolvedCallTargets { get; init; } = new HashSet<(string, int)>();

    /// <summary>The locator calls that stayed opaque, once per body and operation.</summary>
    public IReadOnlyList<(string BodyId, int OperationId)> UnresolvedLocators { get; init; } = [];

    /// <summary>The virtual, interface and delegate calls of each instance with no receiver object at all: unresolved dispatch, which
    /// calls nothing the heap has (R1).</summary>
    public IReadOnlySet<(string Instance, int Operation)> UnresolvedDispatches { get; init; } = new HashSet<(string, int)>();

    /// <summary>For each unresolved dispatch, the receiver objects whose implementation has no body, each with the type of the run's own
    /// declaring that implementation, whose state alone the call sees through it (R1); null where no type of the run's own declares it.</summary>
    public IReadOnlyDictionary<(string Instance, int Operation), IReadOnlySet<(string Region, string? DeclaringTypeKey)>> UnresolvedDispatchReceivers { get; init; } =
        new Dictionary<(string, int), IReadOnlySet<(string, string?)>>();

    /// <summary>For each <c>GetOrAdd</c> or <c>AddOrUpdate</c> a factory of which ran no body, what those factories are handed: the key,
    /// the value the dictionary holds, the overload's argument (R11). Only an update factory is handed the value.</summary>
    public IReadOnlyDictionary<(string Instance, int Operation), IReadOnlySet<IrFactoryInput>> UnresolvedFactoryInputs { get; init; } =
        new Dictionary<(string, int), IReadOnlySet<IrFactoryInput>>();

    /// <summary>For each known call a fated delegate of which is an unresolved dispatch, the objects those delegates are handed (R1, R3).</summary>
    public IReadOnlyDictionary<(string Instance, int Operation), IReadOnlySet<string>> UnresolvedFateInputs { get; init; } =
        new Dictionary<(string, int), IReadOnlySet<string>>();

    /// <summary>The delegates handed to unresolved calls, each run in an unknown execution of its own (R3), by region; and those a
    /// known call runs in one by its model's <c>unknown-execution</c> fate.</summary>
    public IReadOnlyList<DelegateHandoff> DelegateHandoffs { get; init; } = [];

    /// <summary>The delegates known calls run by their model's <c>startup</c> fate: in startup for a call startup makes, in an unknown
    /// execution for a call any other execution makes (R3).</summary>
    public IReadOnlyList<StartupDelegate> StartupDelegates { get; init; } = [];

    /// <summary>The objects that keep a delegate by a known call's <c>holder</c> fate: a call without a body on one runs what it keeps,
    /// and is no unresolved call on it (R4).</summary>
    public IReadOnlySet<string> Holders { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The library sequences and groupings the heap knows, by region (R5). One in <see cref="UnknownIterators"/> escaped, and is
    /// enumerated by an unknown execution as well.</summary>
    public IReadOnlyDictionary<string, LibrarySequence> LibrarySequences { get; init; } = new Dictionary<string, LibrarySequence>(StringComparer.Ordinal);

    /// <summary>What the analysis could not know about a region: a symbolic parameter's unknown caller, a registration whose
    /// factory or instance gives more than one object or an unknown one.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RegionUncertainties { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The objects of each <c>GetServices</c> result region, in the registrations' total order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Collections { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The regions whose construction runs at startup: the container, whose constructions are the registration-holding
    /// members and the entry point, and the objects of instance registrations.</summary>
    public IReadOnlySet<string> StartupRegions { get; init; } = new HashSet<string>();

    /// <summary>The instances whose locator calls resolved each object that has no execution of its own: a transient, or a scoped
    /// object of a created scope. Such an object is created where those instances run.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> LocatorCreators { get; init; } = new Dictionary<string, IReadOnlySet<string>>();

    /// <summary>The operations that trigger the constructions of regions, in instance id, operation and region order.</summary>
    public IReadOnlyList<RegionTrigger> RegionTriggers { get; init; } = [];

    /// <summary>The entry instance of each root, by stable root id.</summary>
    public IReadOnlyDictionary<string, string> RootInstances { get; }

    /// <summary>The spawns of every instance, in caller and operation order.</summary>
    public IReadOnlyList<SpawnSite> Spawns { get; init; } = [];

    public IReadOnlyList<TimerCallbackSite> TimerCallbacks { get; init; } = [];

    public IReadOnlyList<AsyncSpawnSite> AsyncSpawns { get; init; } = [];

    /// <summary>The tail region of each handle whose work returns a task: what <c>Unwrap()</c> and an await of the awaited handle give.</summary>
    public IReadOnlyDictionary<string, string> Tails { get; init; } = new Dictionary<string, string>();

    /// <summary>The antecedent regions of each continuation handle.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> Antecedents { get; init; } = new Dictionary<string, IReadOnlySet<string>>();

    /// <summary>The group of each <c>Task.WhenAll</c> result region.</summary>
    public IReadOnlyDictionary<string, TaskGroup> TaskGroups { get; init; } = new Dictionary<string, TaskGroup>();

    /// <summary>The storage region of a static field as an instance's substitution names it.</summary>
    /// <param name="instanceId">The instance whose substitution names the field's type.</param>
    /// <param name="field">The static field.</param>
    public string StaticRegionOf(string instanceId, IrFieldRef field) => _staticRegion(instanceId, field);

    /// <summary>The fields (and <c>[]</c>) through which a region points to other regions.</summary>
    /// <param name="regionId">The region.</param>
    public IReadOnlyList<string> FieldsOf(string regionId) => _fieldsOf(regionId);

    /// <summary>The regions a delegate region captures: its receiver and the cells of the variables it closes over.</summary>
    /// <param name="regionId">The delegate region.</param>
    public IReadOnlySet<string> DelegateCaptures(string regionId) => _delegateCaptures(regionId);

    /// <summary>The regions an abstract value of an instance's summary points to.</summary>
    /// <param name="instanceId">The instance whose summary the value belongs to.</param>
    /// <param name="value">The abstract value.</param>
    public IReadOnlySet<string> Resolve(string instanceId, AbstractValue value) =>
        value is RegionValue region ? new HashSet<string>(StringComparer.Ordinal) { region.RegionId } : _resolve(instanceId, value);

    /// <summary>The regions a field of a region points to, including what open regions of its group store. The field is a slot key
    /// as <see cref="FieldsOf"/> gives it, <c>[]</c>, or a bare field name, which joins the slots of every declaring type with that
    /// name.</summary>
    /// <param name="regionId">The region.</param>
    /// <param name="field">The field: a slot key, <c>[]</c>, or a bare field name.</param>
    public IReadOnlySet<string> PointsTo(string regionId, string field) => _load(regionId, field);

    /// <summary>The regions the capture cell of a symbol key in a member-body instance points to.</summary>
    /// <param name="ownerInstanceId">The member-body instance owning the cell.</param>
    /// <param name="symbolKey">The captured symbol's key.</param>
    public IReadOnlySet<string> Cell(string ownerInstanceId, string symbolKey) => _cell(ownerInstanceId, symbolKey);
}

/// <summary>The regions of a solved heap: those the solve made, in its order, for every enumeration and count, and also those queries
/// named after the solve, for a lookup by id (ADR 0016).</summary>
/// <param name="solved">The regions the solve made.</param>
/// <param name="named">The regions queries named after the solve.</param>
internal sealed class SolvedRegions(IReadOnlyDictionary<string, HeapRegion> solved, IReadOnlyDictionary<string, HeapRegion> named)
    : IReadOnlyDictionary<string, HeapRegion>
{
    public HeapRegion this[string key] => solved.TryGetValue(key, out var region) ? region : named[key];
    public IEnumerable<string> Keys => solved.Keys;
    public IEnumerable<HeapRegion> Values => solved.Values;
    public int Count => solved.Count;
    public bool ContainsKey(string key) => solved.ContainsKey(key) || named.ContainsKey(key);
    public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out HeapRegion value) =>
        solved.TryGetValue(key, out value) || named.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<string, HeapRegion>> GetEnumerator() => solved.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Andersen-style propagation over <c>(method, context)</c> instances created on demand from the roots and the constructions
/// they trigger. An instance method's context is its receiver region, a static method's its call site; allocations take the
/// instance's context. Contexts beyond <see cref="AnalysisLimits.MaxContextsPerMethod"/>, a substitution naming an open type
/// parameter, and a call-graph cycle that keeps changing for more than <see cref="AnalysisLimits.MaxSccIterations"/> rounds go
/// to one merged context per body. Points-to sets only grow over a finite universe, so the rounds reach a fixpoint.
/// </summary>
public static partial class WholeProgram
{
    public static HeapSolution Solve(ScopeProgram program, AnalysisLimits limits, CancellationToken cancellationToken) =>
        Solve(program, limits, cancellationToken, new());

    internal static HeapSolution Solve(ScopeProgram program, AnalysisLimits limits, CancellationToken cancellationToken, SolverWorklistOptions options) =>
        new Solver(program, limits, cancellationToken, options).Run();

    private static readonly Regex ASSEMBLY_PREFIX = new(@"(?<=^|[<\s,])[^<>,\s:\[\]*]+:");

    /// <summary>A type key as <c>SymbolNames.Type</c> displays it: every assembly prefix removed.</summary>
    /// <param name="typeKey">The assembly-aware type key.</param>
    internal static string DisplayType(string typeKey) => ASSEMBLY_PREFIX.Replace(typeKey, "");

    /// <summary>The reason of a call edge from a locator call to the constructor or factory instances of the object it resolved.</summary>
    public const string CONSTRUCTION_REASON = "construction";

    private const string SERVICE_PROVIDER_TYPE = ":System.IServiceProvider";

    private const string SERVICE_COLLECTION_TYPE = ":Microsoft.Extensions.DependencyInjection.IServiceCollection";

    /// <summary>Where a DI resolution happens: inside one HTTP invocation, in the root scope, or in the scope object a
    /// <c>CreateScope</c> call created.</summary>
    /// <param name="InvocationRootId">The root of the HTTP invocation the resolution happens in, if any.</param>
    /// <param name="CreatedScope">The scope region a <c>CreateScope</c> call created, if the resolution happens in one.</param>
    private sealed record ResolutionScope(string? InvocationRootId, string? CreatedScope = null)
    {
        internal string Context => CreatedScope is { } created ? $"scope:{created}"
            : InvocationRootId is { } rootId ? $"invocation:root:{rootId}"
            : "root-scope";
    }

    /// <summary>A factory or instance registration's region: the scope its factory runs for, the factory instances, and whether
    /// its object fell back to the region itself because nothing flowed to it.</summary>
    /// <param name="region">The registration's region.</param>
    /// <param name="registration">The registration.</param>
    /// <param name="scope">The scope its factory runs for.</param>
    private sealed class RegistrationState(string region, DiRegistration registration, ResolutionScope scope) : ITrackedState
    {
        internal string Region { get; } = region;
        internal DiRegistration Registration { get; } = registration;
        internal ResolutionScope Scope { get; } = scope;
        internal TrackedSet<string> Instances { get; } = new(StringComparer.Ordinal);
        private readonly TrackedValue<bool> _defaulted = new();
        internal bool Defaulted { get => _defaulted.Value; set => _defaulted.Value = value; }

        public void Attach(StateKey key, Action<StateKey> read, Action<StateKey> write)
        {
            Instances.Attach(key.Member("RegistrationState.Instances"), read, write);
            _defaulted.Attach(key.Member("RegistrationState.Defaulted"), read, write);
        }
    }

    private sealed class InstanceState(string id, string bodyId, string context, IReadOnlyDictionary<string, string> substitution,
                                       bool isMerged, bool isReceiverless, MethodSummary summary) : ITrackedState
    {
        internal string Id { get; } = id;
        internal string BodyId { get; } = bodyId;
        internal string Context { get; } = context;
        internal IReadOnlyDictionary<string, string> Substitution { get; } = substitution;
        internal bool IsMerged { get; } = isMerged;
        internal bool IsReceiverless { get; } = isReceiverless;
        internal MethodSummary Summary { get; } = summary;
        internal TrackedSet<string> Receivers { get; } = new(StringComparer.Ordinal);
        internal TrackedSet<string> CellOwners { get; } = new(StringComparer.Ordinal);
        internal TrackedMap<int, TrackedSet<string>> Parameters { get; } = [];
        internal TrackedMap<int, TrackedSet<string>> CallResults { get; } = [];

        /// <summary>The calls whose result, and the returns of this instance, may come from an origin points-to does not follow.</summary>
        internal TrackedSet<int> UnfollowedCallResults { get; } = [];

        /// <summary>The awaits and <c>Unwrap()</c> calls that read a task whose completion may come from such an origin.</summary>
        internal TrackedSet<int> UnfollowedCompletions { get; } = [];

        /// <summary>The known calls and channels whose <c>completion(…)</c> value may come from such an origin, one channel apart from
        /// another.</summary>
        internal TrackedSet<(int Operation, string Channel)> UnfollowedModelCompletions { get; } = [];

        /// <summary>The calls whose receiver may come from such an origin, so their edges are not all the targets they may run.</summary>
        internal TrackedSet<int> UnresolvedCallTargets { get; } = [];
        private readonly TrackedValue<bool> _returnsUnfollowed = new();
        internal bool ReturnsUnfollowed { get => _returnsUnfollowed.Value; set => _returnsUnfollowed.Value = value; }
        internal TrackedMap<(int Operation, int Ordinal), TrackedSet<string>> RefResults { get; } = [];
        internal TrackedSet<string> Returns { get; } = new(StringComparer.Ordinal);
        internal TrackedMap<int, TrackedSet<(string Region, string Slot)>> ParameterLocations { get; } = [];
        internal TrackedSet<(string Region, string Slot)> ReturnedLocations { get; } = [];

        /// <summary>What an iterator instance's <c>yield return</c>s hand out.</summary>
        internal TrackedSet<string> Yields { get; } = new(StringComparer.Ordinal);
        internal TrackedMap<int, TrackedSet<string>> RefParameters { get; } = [];

        /// <summary>The HTTP invocations, by root id, whose request this instance runs in.</summary>
        internal TrackedSet<string> Requests { get; } = new(StringComparer.Ordinal);

        public void Attach(StateKey key, Action<StateKey> read, Action<StateKey> write)
        {
            Receivers.Attach(key.Member("InstanceState.Receivers"), read, write);
            CellOwners.Attach(key.Member("InstanceState.CellOwners"), read, write);
            Parameters.Attach(key.Member("InstanceState.Parameters"), read, write);
            CallResults.Attach(key.Member("InstanceState.CallResults"), read, write);
            UnfollowedCallResults.Attach(key.Member("InstanceState.UnfollowedCallResults"), read, write);
            UnfollowedCompletions.Attach(key.Member("InstanceState.UnfollowedCompletions"), read, write);
            UnfollowedModelCompletions.Attach(key.Member("InstanceState.UnfollowedModelCompletions"), read, write);
            UnresolvedCallTargets.Attach(key.Member("InstanceState.UnresolvedCallTargets"), read, write);
            RefResults.Attach(key.Member("InstanceState.RefResults"), read, write);
            Returns.Attach(key.Member("InstanceState.Returns"), read, write);
            ParameterLocations.Attach(key.Member("InstanceState.ParameterLocations"), read, write);
            ReturnedLocations.Attach(key.Member("InstanceState.ReturnedLocations"), read, write);
            Yields.Attach(key.Member("InstanceState.Yields"), read, write);
            RefParameters.Attach(key.Member("InstanceState.RefParameters"), read, write);
            Requests.Attach(key.Member("InstanceState.Requests"), read, write);
            _returnsUnfollowed.Attach(key.Member("InstanceState.ReturnsUnfollowed"), read, write);
        }
    }

    private sealed class DelegateState(string target, bool isNestedBody, string? containingTypeKey, IReadOnlyList<string> methodTypeArguments,
                                       IReadOnlyDictionary<string, string> ownerSubstitution, bool isNonVirtual) : ITrackedState
    {
        internal string Target { get; } = target;
        internal bool IsNestedBody { get; } = isNestedBody;

        /// <summary>The method group was named through <c>base</c>: its target runs on each receiver without dispatch.</summary>
        internal bool IsNonVirtual { get; } = isNonVirtual;
        internal string? ContainingTypeKey { get; } = containingTypeKey;
        internal IReadOnlyList<string> MethodTypeArguments { get; } = methodTypeArguments;
        internal IReadOnlyDictionary<string, string> OwnerSubstitution { get; } = ownerSubstitution;
        internal TrackedSet<string> CellOwners { get; } = new(StringComparer.Ordinal);
        internal TrackedSet<string> CapturedReceivers { get; } = new(StringComparer.Ordinal);
        internal TrackedSet<string> CapturedKeys { get; } = new(StringComparer.Ordinal);

        public void Attach(StateKey key, Action<StateKey> read, Action<StateKey> write)
        {
            CellOwners.Attach(key.Member("DelegateState.CellOwners"), read, write);
            CapturedReceivers.Attach(key.Member("DelegateState.CapturedReceivers"), read, write);
            CapturedKeys.Attach(key.Member("DelegateState.CapturedKeys"), read, write);
        }
    }

    /// <summary>What a spawn, async spawn or timer callback site has run so far: a spawn has a <see cref="Kind"/> and a call, a
    /// timer callback an <see cref="Action"/>, and only a spawn a <see cref="Tail"/>.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="callOperationId">The callOperationId.</param>
    /// <param name="action">The action.</param>
    private sealed class SiteState(IrSpawnKind? kind, int callOperationId, IrTimerAction? action = null) : ITrackedState
    {
        internal IrSpawnKind? Kind { get; } = kind;
        internal int CallOperationId { get; } = callOperationId;
        internal IrTimerAction? Action { get; } = action;
        internal TrackedSet<string> Handles { get; } = new(StringComparer.Ordinal);
        private readonly TrackedValue<string?> _tail = new();
        internal string? Tail { get => _tail.Value; set => _tail.Value = value; }
        internal TrackedSet<(string Instance, SpawnRole Role)> Callees { get; } = [];

        public void Attach(StateKey key, Action<StateKey> read, Action<StateKey> write)
        {
            Handles.Attach(key.Member("SiteState.Handles"), read, write);
            Callees.Attach(key.Member("SiteState.Callees"), read, write);
            _tail.Attach(key.Member("SiteState.Tail"), read, write);
        }
    }

    private sealed partial class Solver
    {
        private static readonly IReadOnlyDictionary<string, string> NO_SUBSTITUTION = new TrackedMap<string, string>();

        /// <summary>The synthetic field of a <c>Thread</c> region holding the work its constructor bound.</summary>
        private const string THREAD_WORK_FIELD = "<thread-work>";

        /// <summary>The synthetic slot of a task region holding the value the task completes with (<see cref="Complete"/>).</summary>
        private const string COMPLETION_FIELD = PathValue.COMPLETION;

        /// <summary>The path of a model value a library sequence enumerates as a source: it delivers nothing through a channel of the call.</summary>
        private const string SOURCE_PATH = "source";

        private readonly ScopeProgram _scope;
        private readonly ProgramIndex _program;
        private readonly AnalysisLimits _limits;
        private readonly TrackedMap<string, string> _memberOfNestedBody = new(StringComparer.Ordinal);
        private readonly TrackedMap<string, TrackedSet<string>> _capturedKeysOfMember = new(StringComparer.Ordinal);
        private readonly TrackedMap<string, HeapRegion> _regions = new(StringComparer.Ordinal);
        private readonly TrackedMap<string, TrackedList<string>> _groups = new(StringComparer.Ordinal);
        private readonly TrackedMap<(string Region, string Field), TrackedSet<string>> _fields = [];
        private readonly TrackedMap<string, TrackedList<string>> _fieldsByRegion = new(StringComparer.Ordinal);
        private readonly TrackedMap<(string Instance, int Operation), TrackedMap<IrLibraryCall, SummaryOpaqueCall>> _keeperCalls = [];
        private readonly TrackedMap<(string Instance, int Operation, int Keeper), TrackedSet<string>> _libraryKeeping = [];
        private readonly TrackedSet<(string Instance, int Operation, int Keeper)> _keptFallbacks = [];
        private readonly TrackedMap<(string Owner, string Key), TrackedSet<string>> _cells = [];
        private readonly TrackedMap<string, DelegateState> _delegates = new(StringComparer.Ordinal);
        private readonly TrackedMap<string, InstanceState> _instances = new(StringComparer.Ordinal);
        private readonly TrackedList<string> _instanceOrder = [];
        private readonly TrackedMap<string, int> _contextCounts = new(StringComparer.Ordinal);
        private readonly TrackedSet<string> _mergedBodies = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sccHandled = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _changedRounds = new(StringComparer.Ordinal);
        private readonly TrackedSet<(string Caller, int Operation, string Callee, string Reason)> _edges = [];
        private readonly TrackedMap<string, TrackedList<(string Caller, int Operation, string Callee, string Reason)>> _edgesByCallee = new(StringComparer.Ordinal);
        private readonly TrackedMap<string, TrackedList<(string Caller, int Operation, string Callee, string Reason)>> _edgesByCaller = new(StringComparer.Ordinal);
        private readonly TrackedMap<(string Caller, int Operation), TrackedList<(string Caller, int Operation, string Callee, string Reason)>> _edgesByCall = [];
        private readonly TrackedMap<string, CallEdge> _iteratorObjects = new(StringComparer.Ordinal);
        private readonly TrackedMap<string, (string InstanceId, TrackedSet<string> Instances, TrackedSet<string> Regions)> _typeInitializers =
            new(StringComparer.Ordinal);
        private readonly TrackedMap<string, TrackedList<string>> _constructions = new(StringComparer.Ordinal);

        /// <summary>The holders the heap knows and what each keeps, by delegate region, <see cref="UNRESOLVED_DELEGATE"/> standing for a
        /// delegate no delegate object is known for (R4).</summary>
        private readonly TrackedMap<string, TrackedMap<string, HeldDelegate>> _held = new(StringComparer.Ordinal);

        /// <summary>What each region's fields point to, built once a pass for the reach of a call's arguments; a pass that changes
        /// nothing reads it whole.</summary>
        private Dictionary<string, List<string>>? _reachIndex;

        /// <summary>The library sequences known calls return, and those a model's value creates, by region (R5).</summary>
        private readonly TrackedMap<string, SequenceState> _sequences = new(StringComparer.Ordinal);

        /// <summary>The groupings a model's value creates: each holds its key in its key storage and yields what its element storage
        /// holds (R5).</summary>
        private readonly TrackedSet<string> _groupings = new(StringComparer.Ordinal);

        private readonly TrackedValue<UnknownCalls.Modelled?> _modelledState = new();
        private UnknownCalls.Modelled? Modelled { get => _modelledState.Value; set => _modelledState.Value = value; }
        private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal)
        {
            [HeapCounters.PROPAGATE_PASSES] = 0,
            [HeapCounters.INSTANCE_PROCESSINGS] = 0,
            [HeapCounters.REFERENCE_LOOKUPS] = 0,
        };
        private readonly TrackedMap<string, string> _rootInstances = new(StringComparer.Ordinal);
        private readonly TrackedMap<string, ResolutionScope> _providers = new(StringComparer.Ordinal);
        private readonly TrackedSet<string> _scopeObjects = new(StringComparer.Ordinal);
        private readonly TrackedMap<string, RegistrationState> _registrations = new(StringComparer.Ordinal);
        private readonly TrackedMap<string, string> _factoryInstances = new(StringComparer.Ordinal);
        private readonly TrackedMap<(string BodyId, int OperationId, string Context), string> _registeredAllocations = [];
        private readonly TrackedMap<string, TrackedSet<string>> _regionTypes = new(StringComparer.Ordinal);
        private readonly TrackedSet<(TrackedSet<string> Target, string Region)> _sinks = [];
        private readonly TrackedMap<string, TrackedList<string>> _uncertainties = new(StringComparer.Ordinal);
        private readonly TrackedMap<string, TrackedList<string>> _collections = new(StringComparer.Ordinal);
        private readonly TrackedSet<string> _startupRegions = new(StringComparer.Ordinal);
        private readonly TrackedMap<string, TrackedSet<string>> _locatorCreators = new(StringComparer.Ordinal);
        private readonly TrackedSet<(string InstanceId, int OperationId, string RegionId)> _regionTriggers = [];
        private readonly TrackedMap<(string BodyId, int OperationId), (string BodyId, SummaryOpaqueCall Call)?> _sites = [];
        private readonly TrackedMap<string, DiRegistration[]> _instanceRegistrationsByMember = new(StringComparer.Ordinal);
        private readonly TrackedMap<(string Caller, int Operation), SiteState> _spawns = [];
        private readonly TrackedMap<(string Caller, int Operation), SiteState> _timerCallbacks = [];
        private readonly TrackedMap<(string Caller, int Operation), SiteState> _asyncSpawns = [];
        private readonly TrackedMap<(string Caller, int Operation), (InstanceState Caller, CallTransfer Call)> _receiverlessCalls = [];
        private readonly TrackedSet<(string Caller, int Operation)> _receiverlessFallbacks = [];
        private readonly TrackedMap<(string Caller, int Operation), TrackedSet<string>> _iteratorMemberReceivers = [];
        private readonly TrackedMap<(string Instance, int Operation, int Target), TrackedSet<string>> _libraryStored = [];
        private readonly TypeSafety _typeSafety;
        private readonly TrackedMap<string, string> _tails = new(StringComparer.Ordinal);
        private readonly TrackedMap<string, TrackedSet<string>> _antecedents = new(StringComparer.Ordinal);
        private readonly TrackedMap<string, (TrackedSet<string> Members, bool Known)> _taskGroups = new(StringComparer.Ordinal);

        /// <summary>The task regions whose completion may be an object the heap does not follow: what completes them is unfollowed where
        /// it is produced, or their producer's code is not seen in full.</summary>
        private readonly TrackedSet<string> _unfollowedCompletions = new(StringComparer.Ordinal);

        /// <summary>The keepers a known call made keep a <c>completion(…)</c> value that may be an object the heap does not follow: what
        /// a later call reads as <c>kept:…</c> of one of them may be such an object (R2).</summary>
        private readonly TrackedSet<string> _unfollowedKept = new(StringComparer.Ordinal);

        /// <summary>The keepers a known call made keep any value that may be an object the heap does not follow. Only a task's completion
        /// reads it: a synchronous <c>kept:…</c> keeps no unknown source of what it names, a rule this set leaves as it is (R2).</summary>
        private readonly TrackedSet<string> _unfollowedKeptValues = new(StringComparer.Ordinal);

        /// <summary>What completed each task region, for the export only (<see cref="HeapSolution.TaskCompleters"/>): no rule of the solve
        /// reads it.</summary>
        private readonly TrackedSet<(string Task, TaskCompleter Completer)> _completers = [];
        private int _changes;
        private readonly CancellationToken _cancellationToken;
        private readonly TrackedValue<bool> _solvingState = new(true);
        private bool Solving { get => _solvingState.Value; set => _solvingState.Value = value; }
        // Regions that queries of the solved heap named and the solve did not make: no map of the solve holds one.
        private readonly ConcurrentDictionary<string, HeapRegion> _named = new(StringComparer.Ordinal);
        private int _queries;

        /// <summary>Whether a query of the solved heap is running: it names what the solve did not make and writes nothing (open
        /// question 110).</summary>
        private bool Querying => Volatile.Read(ref _queries) != 0;

        /// <summary>The query, run as a query of the solved heap: <see cref="Querying"/> holds while it runs.</summary>
        /// <param name="query">The query.</param>
        private Func<TArgument, TResult> Query<TArgument, TResult>(Func<TArgument, TResult> query) => argument =>
        {
            Interlocked.Increment(ref _queries);
            try
            {
                return query(argument);
            }
            finally
            {
                Interlocked.Decrement(ref _queries);
            }
        };

        /// <summary>The query with two arguments, run as a query of the solved heap: <see cref="Querying"/> holds while it runs.</summary>
        /// <param name="query">The query.</param>
        private Func<TFirst, TSecond, TResult> Query<TFirst, TSecond, TResult>(Func<TFirst, TSecond, TResult> query) => (first, second) =>
        {
            Interlocked.Increment(ref _queries);
            try
            {
                return query(first, second);
            }
            finally
            {
                Interlocked.Decrement(ref _queries);
            }
        };

        private void CheckCancellation()
        {
            if (Solving)
                _cancellationToken.ThrowIfCancellationRequested();
        }

        internal Solver(ScopeProgram scope, AnalysisLimits limits, CancellationToken cancellationToken, SolverWorklistOptions options)
        {
            _cancellationToken = cancellationToken;
            _scope = scope;
            _program = scope.Program;
            _typeSafety = new TypeSafety(scope.Program, scope.MetadataSupertypes);
            _limits = limits;
            _worklistOptions = options;
            _memberOfNestedBody.Attach(new StateKey("_memberOfNestedBody"), ReadState, WroteState);
            _capturedKeysOfMember.Attach(new StateKey("_capturedKeysOfMember"), ReadState, WroteState);
            _regions.Attach(new StateKey("_regions"), ReadState, WroteState);
            _groups.Attach(new StateKey("_groups"), ReadState, WroteState);
            _fields.Attach(new StateKey("_fields"), ReadState, WroteState);
            _fieldsByRegion.Attach(new StateKey("_fieldsByRegion"), ReadState, WroteState);
            _keeperCalls.Attach(new StateKey("_keeperCalls"), ReadState, WroteState);
            _libraryKeeping.Attach(new StateKey("_libraryKeeping"), ReadState, WroteState);
            _keptFallbacks.Attach(new StateKey("_keptFallbacks"), ReadState, WroteState);
            _cells.Attach(new StateKey("_cells"), ReadState, WroteState);
            _delegates.Attach(new StateKey("_delegates"), ReadState, WroteState);
            _instances.Attach(new StateKey("_instances"), ReadState, WroteState);
            _instanceOrder.Attach(new StateKey("_instanceOrder"), ReadState, WroteState);
            _contextCounts.Attach(new StateKey("_contextCounts"), ReadState, WroteState);
            _mergedBodies.Attach(new StateKey("_mergedBodies"), ReadState, WroteState);
            _edges.Attach(new StateKey("_edges"), ReadState, WroteState);
            _edgesByCallee.Attach(new StateKey("_edgesByCallee"), ReadState, WroteState);
            _edgesByCaller.Attach(new StateKey("_edgesByCaller"), ReadState, WroteState);
            _edgesByCall.Attach(new StateKey("_edgesByCall"), ReadState, WroteState);
            _iteratorObjects.Attach(new StateKey("_iteratorObjects"), ReadState, WroteState);
            _typeInitializers.Attach(new StateKey("_typeInitializers"), ReadState, WroteState);
            _constructions.Attach(new StateKey("_constructions"), ReadState, WroteState);
            _held.Attach(new StateKey("_held"), ReadState, WroteState);
            _sequences.Attach(new StateKey("_sequences"), ReadState, WroteState);
            _groupings.Attach(new StateKey("_groupings"), ReadState, WroteState);
            _modelledState.Attach(new StateKey("_modelledState"), ReadState, WroteState);
            _rootInstances.Attach(new StateKey("_rootInstances"), ReadState, WroteState);
            _providers.Attach(new StateKey("_providers"), ReadState, WroteState);
            _scopeObjects.Attach(new StateKey("_scopeObjects"), ReadState, WroteState);
            _registrations.Attach(new StateKey("_registrations"), ReadState, WroteState);
            _factoryInstances.Attach(new StateKey("_factoryInstances"), ReadState, WroteState);
            _registeredAllocations.Attach(new StateKey("_registeredAllocations"), ReadState, WroteState);
            _regionTypes.Attach(new StateKey("_regionTypes"), ReadState, WroteState);
            _sinks.Attach(new StateKey("_sinks"), ReadState, WroteState);
            _uncertainties.Attach(new StateKey("_uncertainties"), ReadState, WroteState);
            _collections.Attach(new StateKey("_collections"), ReadState, WroteState);
            _startupRegions.Attach(new StateKey("_startupRegions"), ReadState, WroteState);
            _locatorCreators.Attach(new StateKey("_locatorCreators"), ReadState, WroteState);
            _regionTriggers.Attach(new StateKey("_regionTriggers"), ReadState, WroteState);
            _sites.Attach(new StateKey("_sites"), ReadState, WroteState);
            _instanceRegistrationsByMember.Attach(new StateKey("_instanceRegistrationsByMember"), ReadState, WroteState);
            _spawns.Attach(new StateKey("_spawns"), ReadState, WroteState);
            _timerCallbacks.Attach(new StateKey("_timerCallbacks"), ReadState, WroteState);
            _asyncSpawns.Attach(new StateKey("_asyncSpawns"), ReadState, WroteState);
            _receiverlessCalls.Attach(new StateKey("_receiverlessCalls"), ReadState, WroteState);
            _receiverlessFallbacks.Attach(new StateKey("_receiverlessFallbacks"), ReadState, WroteState);
            _iteratorMemberReceivers.Attach(new StateKey("_iteratorMemberReceivers"), ReadState, WroteState);
            _libraryStored.Attach(new StateKey("_libraryStored"), ReadState, WroteState);
            _tails.Attach(new StateKey("_tails"), ReadState, WroteState);
            _antecedents.Attach(new StateKey("_antecedents"), ReadState, WroteState);
            _taskGroups.Attach(new StateKey("_taskGroups"), ReadState, WroteState);
            _unfollowedCompletions.Attach(new StateKey("_unfollowedCompletions"), ReadState, WroteState);
            _unfollowedKept.Attach(new StateKey("_unfollowedKept"), ReadState, WroteState);
            _unfollowedKeptValues.Attach(new StateKey("_unfollowedKeptValues"), ReadState, WroteState);
            _completers.Attach(new StateKey("_completers"), ReadState, WroteState);
            _solvingState.Attach(new StateKey("_solvingState"), ReadState, WroteState);
        }

        private void Initialize()
        {
            foreach (var method in _program.Methods)
            {
                CheckCancellation();
                foreach (var nested in method.NestedBodyIds)
                {
                    CheckCancellation();
                    _memberOfNestedBody.TryAdd(nested, method.MethodId);
                }
            }

            foreach (var group in RegistrationsWithBodies(DiRegistrationForm.Instance).GroupBy(registration => registration.BodyId!, StringComparer.Ordinal))
            {
                CheckCancellation();
                _instanceRegistrationsByMember.Add(group.Key, group.ToArray());
            }
        }

        internal HeapSolution Run()
        {
            try
            {
                CheckCancellation();
                Initialize();
                Seed();
                do
                {
                    CheckCancellation();
                    Propagate();
                }
                while (DefaultEmptyRegistrations() || CreateReceiverlessFallbacks() || CreateKeeperFallbacks());

                if (_worklistOptions.VerifyConvergence)
                    VerifyConvergence();
                var result = Result();
                CheckCancellation();
                return result;
            }
            catch (OperationCanceledException error)
            {
                throw new EngineStageCancelledException("heap", new Dictionary<string, int>(_counters, StringComparer.Ordinal),
                                                        _cancellationToken, error);
            }
            finally
            {
                // Queries retained by HeapSolution belong to their later stage, not to this solve.
                Solving = false;
            }
        }

        private bool CreateKeeperFallbacks()
        {
            var wave = _keeperCalls.SelectMany(pair => pair.Value.Values.SelectMany(call => call.Library!.Keeps.Keys.Select(keeper =>
                (Caller: _instances[pair.Key.Instance], Call: call, Keeper: keeper))))
                .Where(item => item.Keeper != IrLibraryCall.RESULT &&
                               !_keptFallbacks.Contains((item.Caller.Id, item.Call.OperationId, item.Keeper)) &&
                               Eval(item.Caller, ArgumentOf(item.Call, item.Keeper)).Count == 0).ToArray();
            foreach (var item in wave)
            {
                CheckCancellation();
                _keptFallbacks.Add((item.Caller.Id, item.Call.OperationId, item.Keeper));
            }
            foreach (var item in wave)
            {
                CheckCancellation();
                Add(Field(KeeperStore(item.Call, item.Keeper), PathValue.KEPT),
                    _libraryKeeping.GetValueOrDefault((item.Caller.Id, item.Call.OperationId, item.Keeper)) ?? []);
            }
            return wave.Length != 0;
        }

        private void Propagate()
        {
            while (true)
            {
                CheckCancellation();
                _counters[HeapCounters.PROPAGATE_PASSES]++;
                var before = _changes;
                // Rebuilt outputs retain each skipped instance's contribution, in creation order.
                _rebuilding = new();
                _reachIndex = null;
                for (var index = 0; index < _instanceOrder.Count; index++)
                {
                    CheckCancellation();
                    ProcessOrSkip(_instances[_instanceOrder[index]]);
                }

                AssembleContributions();

                EscapeCapturedHolders();

                foreach (var state in _registrations.Values.ToArray())
                {
                    CheckCancellation();
                    ConstructFactory(state);
                }
                foreach (var (target, region) in _sinks.ToArray())
                {
                    CheckCancellation();
                    Add(target, Object(region));
                }

                if (_changes == before)
                    break;
            }
        }

        /// <summary>A factory or instance registration nothing flowed to keeps its own region, with no created type.</summary>
        private bool DefaultEmptyRegistrations()
        {
            var defaulted = false;
            foreach (var state in _registrations.Values.Where(state => !state.Defaulted).ToArray())
            {
                CheckCancellation();
                if (Object(state.Region).Count != 0)
                    continue;
                state.Defaulted = true;
                defaulted = true;
                _changes++;
            }

            return defaulted;
        }

        private bool CreateReceiverlessFallbacks()
        {
            // Decide the whole wave before making any fallback: one fallback must not supply another call's receiver in this wave.
            var pending = _receiverlessCalls.Where(site => !_receiverlessFallbacks.Contains(site.Key) &&
                                                         !DeadReceiver(site.Value.Caller, site.Value.Call,
                                                                       site.Value.Call.TargetContainingTypeKey is { } key
                                                                           ? ProgramIndex.Substitute(key, site.Value.Caller.Substitution)
                                                                           : null) &&
                                                         Eval(site.Value.Caller, site.Value.Call.Receivers).Count == 0)
                                            .Select(site => site.Value).ToArray();
            foreach (var (caller, call) in pending)
            {
                CheckCancellation();
                var method = _program.Method(call.Target)!;
                var typeArguments = (call.TargetMethodTypeArgumentKeys ?? []).Select(key => ProgramIndex.Substitute(key, caller.Substitution)).ToArray();
                var containing = call.TargetContainingTypeKey is null ? null : ProgramIndex.Substitute(call.TargetContainingTypeKey, caller.Substitution);
                var callee = Instance(call.Target, $"{caller.Context}|{caller.BodyId}#{call.OperationId}",
                                      Substitution(method, containing, typeArguments), [], [], true);
                Bind(caller, call, callee, "exact");
                _receiverlessFallbacks.Add((caller.Id, call.OperationId));
            }

            return pending.Length != 0;
        }

        private IEnumerable<DiRegistration> RegistrationsWithBodies(DiRegistrationForm form) =>
            _scope.DiIndex.Registrations.Where(registration => registration is { IsSupported: true, BodyId: not null, OperationId: not null,
                                                                                  ServiceTypeKey: not null, ImplementationTypeKey: not null } &&
                                                               registration.Form == form);

        private HeapSolution Result()
        {
            CheckCancellation();
            var reachable = _instances.Values.Select(instance => instance.BodyId).ToTrackedSet(StringComparer.Ordinal);
            // Every lowered body, nested ones included: a lambda the heap never reaches is inventory too.
            var loweredNotReached = _scope.Reachable.Bodies.Keys.Where(body => !reachable.Contains(body))
                                          .Order(StringComparer.Ordinal).ToArray();
            _counters[HeapCounters.NO_RECEIVER_OBJECT] = _rebuilding.NoReceiver.Count;
            _counters[HeapCounters.REACHABLE_BODIES] = reachable.Count;
            _counters[HeapCounters.LOWERED_NOT_REACHED] = loweredNotReached.Length;
            _counters.TryAdd(HeapCounters.MERGED_CONTEXT, 0);
            _counters.TryAdd(HeapCounters.SCC_BUDGET_EXCEEDED, 0);
            var unresolved = UnresolvedLocators();
            _counters[HeapCounters.UNRESOLVED_LOCATOR] = unresolved.Count;
            RegistrationUncertainties();

            var instances = _instances.Values.ToTrackedMap(
                instance => instance.Id,
                instance => new MethodInstance(instance.Id, instance.BodyId, instance.Context, instance.Substitution, instance.IsMerged,
                                               instance.IsReceiverless, instance.Summary, instance.Receivers,
                                               instance.Parameters.ToTrackedMap(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value),
                                               instance.CellOwners),
                StringComparer.Ordinal);
            var executionEdges = _edges.Where(edge => _scope.Reachable.Bodies.GetValueOrDefault(_instances[edge.Callee].BodyId)?.IsIterator != true)
                                       .Select(edge => new CallEdge(edge.Caller, edge.Operation, edge.Callee, edge.Reason)).ToTrackedList();
            var unknownIterators = new TrackedSet<string>(StringComparer.Ordinal);
            bool IsEnumerable(string region) => _iteratorObjects.ContainsKey(region) || _sequences.ContainsKey(region);
            foreach (var instance in _instances.Values.Where(_ => _iteratorObjects.Count != 0 || _sequences.Count != 0))
            {
                CheckCancellation();
                if (!_scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body))
                    continue;
                var calls = body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>().ToTrackedMap(call => call.Id);
                // A task member that completes a task with a value hands it to that task, which carries it as a value handed over
                // directly would be carried (R2): it escapes where the task does.
                var completing = instance.Summary.TaskOperations.Select(task => task.CallOperationId).ToHashSet();
                foreach (var call in instance.Summary.OpaqueCalls.Where(call => !completing.Contains(call.OperationId)))
                {
                    CheckCancellation();
                    if (!calls.TryGetValue(call.OperationId, out var operation))
                        continue;
                    var candidateValues = call.Receivers.Concat(call.Arguments.SelectMany(argument => argument.Values))
                                              .Where(PotentialIteratorValue).ToArray();
                    // A fresh object is no iterator, but one the solve completed as a task — a TaskCompletionSource — carries what it
                    // completes with; it is looked up, never made, so the export adds no region.
                    var completingObjects = SolvedTasks(instance, call.Receivers.Concat(call.Arguments.SelectMany(argument => argument.Values))
                                                                          .OfType<AllocationValue>().ToArray());
                    if (candidateValues.Length == 0 && completingObjects.Count == 0)
                        continue;
                    var handed = Eval(instance, candidateValues);
                    handed.UnionWith(completingObjects);
                    // A task handed over hands over what it completes with, at any depth, as that value handed over directly would be;
                    // a delegate it completes with hands over what it captured, as one a field keeps does (R2).
                    var completed = Carried(handed).Where(region => !handed.Contains(region)).ToArray();
                    var iteratorRegions = handed.Where(IsEnumerable)
                                                .Concat(completed.Where(IsEnumerable))
                                                .Concat(completed.Where(_delegates.ContainsKey).SelectMany(Captures).Where(IsEnumerable))
                                                .Distinct(StringComparer.Ordinal).ToArray();
                    if (iteratorRegions.Length == 0)
                        continue;
                    // A consumer enumerates what it reads deep, names the elements of or copies, and a known call returning a library
                    // sequence keeps what the sequence enumerates; what a call is handed any other way escapes to it (R3, R5). What a
                    // task it is handed completes with is consumed or handed otherwise as that task is.
                    var consumed = Consumed(call);
                    var consumedRegions = consumed.Count == 0
                        ? []
                        : Carried(Eval(instance, call.Arguments.Where(argument => consumed.Contains(argument.ParameterOrdinal))
                                                     .SelectMany(argument => argument.Values)
                                                     .Concat(consumed.Contains(IrLibraryCall.RECEIVER) ? call.Receivers : []).Where(PotentialIteratorValue)));
                    var handedOtherwise = consumed.Count == 0
                        ? []
                        : Carried(Eval(instance, call.Receivers.Where(_ => !consumed.Contains(IrLibraryCall.RECEIVER))
                                                     .Concat(call.Arguments.Where(argument => !consumed.Contains(argument.ParameterOrdinal))
                                                                                     .SelectMany(argument => argument.Values))
                                                     .Where(PotentialIteratorValue)));
                    foreach (var region in iteratorRegions)
                    {
                        CheckCancellation();
                        if (!handed.Contains(region))
                        {
                            // Reached only through a task's completion: what consumes the task enumerates it, anything else lets it escape.
                            if (!consumedRegions.Contains(region) || handedOtherwise.Contains(region))
                                unknownIterators.Add(region);
                            continue;
                        }
                        if (_iteratorMemberReceivers.GetValueOrDefault((instance.Id, call.OperationId))?.Contains(region) == true &&
                            Eval(instance, call.Receivers).Contains(region))
                            continue;
                        if (operation.EnumerationRole == IrEnumerationRole.GetEnumerator &&
                            Eval(instance, call.Receivers).Contains(region))
                        {
                            if (_iteratorObjects.TryGetValue(region, out var iterator))
                                executionEdges.Add(new CallEdge(instance.Id, call.OperationId, iterator.CalleeInstance, "iterator-enumeration"));
                        }
                        else if (operation.EnumerationRole == IrEnumerationRole.None &&
                                 (!consumedRegions.Contains(region) || handedOtherwise.Contains(region)))
                            unknownIterators.Add(region);
                    }
                }
            }

            // What a consumer other than a `foreach` enumerates, directly or as a source of a library sequence it enumerates (R5).
            var enumerations = executionEdges.ToTrackedSet();
            foreach (var (consumer, operation, region) in _rebuilding.IteratorEnumerations)
            {
                CheckCancellation();
                var edge = new CallEdge(consumer, operation, _iteratorObjects[region].CalleeInstance, "iterator-enumeration");
                if (enumerations.Add(edge))
                    executionEdges.Add(edge);
            }

            // An iterator or a library sequence escapes when a field keeps it, or keeps a delegate that captured it: whoever calls that
            // delegate enumerates it (SPEC TD-060b). The delegate itself, stored without a visible call, runs nowhere of its own. What a
            // library sequence or a grouping holds is what it yields, which is no field of the run's. A task's completion slot is no field
            // either: the task carries what it completes with, which escapes where the task does, as a value handed over directly would (R2).
            foreach (var (key, values) in _fields)
            {
                CheckCancellation();
                if (_sequences.ContainsKey(key.Region) || _groupings.Contains(key.Region) || key.Field == COMPLETION_FIELD)
                    continue;
                var carried = Carried(values);
                unknownIterators.UnionWith(carried.Where(IsEnumerable));
                unknownIterators.UnionWith(carried.Where(_delegates.ContainsKey).SelectMany(Captures).Where(IsEnumerable));
            }

            // What an unknown enumeration of a library sequence runs: its delegates, and what enumerating its sources runs.
            TrackedSet<string> EnumerationInstances(string region, TrackedSet<string> visited)
            {
                CheckCancellation();
                var instances = new TrackedSet<string>(StringComparer.Ordinal);
                if (_iteratorObjects.TryGetValue(region, out var iterator))
                    instances.Add(iterator.CalleeInstance);
                else if (_sequences.TryGetValue(region, out var sequence) && visited.Add(region))
                {
                    instances.UnionWith(sequence.Callees);
                    foreach (var source in sequence.Sources)
                    {
                        CheckCancellation();
                        instances.UnionWith(EnumerationInstances(source, visited));
                    }
                }

                return instances;
            }

            var librarySequences = _sequences.Values.Select(sequence => new LibrarySequence(
                                                 sequence.Region, sequence.Creator, sequence.Operation, !sequence.IsNested, false,
                                                 sequence.Sources.Order(StringComparer.Ordinal).ToArray(),
                                                 Load(sequence.Region, PathValue.ELEMENT).Order(StringComparer.Ordinal).ToArray(), [],
                                                 EnumerationInstances(sequence.Region, new TrackedSet<string>(StringComparer.Ordinal))
                                                     .Order(StringComparer.Ordinal).ToArray())
                                             {
                                                 ReturnedSources = sequence.ReturnedSources.Order(StringComparer.Ordinal).ToArray(),
                                                 Unresolved = sequence.Unresolved.OrderBy(pair => pair.Key.Parameter)
                                                                      .ThenBy(pair => pair.Key.Region, StringComparer.Ordinal)
                                                                      .Select(pair => new SequenceDispatch(
                                                                          pair.Value.Callee,
                                                                          Eval(_instances[sequence.Creator], pair.Value.Inputs.SelectMany(input => input.Values))
                                                                              .Order(StringComparer.Ordinal).ToArray(),
                                                                          pair.Value.Receivers))
                                                                      .ToArray()
                                             })
                                             .Concat(_groupings.Select(grouping => new LibrarySequence(
                                                 grouping, "", 0, false, true, [],
                                                 Load(grouping, PathValue.ELEMENT).Order(StringComparer.Ordinal).ToArray(),
                                                 Load(grouping, PathValue.KEYS).Order(StringComparer.Ordinal).ToArray(), [])))
                                             .ToTrackedMap(sequence => sequence.RegionId, StringComparer.Ordinal);

            bool PotentialIteratorValue(AbstractValue value)
            {
                CheckCancellation();
                return value switch
                {
                    AllocationValue or DelegateCreationValue => false,
                    PathValue path => PotentialIteratorValue(path.Base),
                    _ => true
                };
            }

            return new HeapSolution(
                new SolvedRegions(_regions, _named),
                instances,
                _edges.OrderBy(edge => edge.Caller, StringComparer.Ordinal).ThenBy(edge => edge.Operation)
                      .ThenBy(edge => edge.Callee, StringComparer.Ordinal)
                      .Select(edge => new CallEdge(edge.Caller, edge.Operation, edge.Callee, edge.Reason)).ToArray(),
                _typeInitializers.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                 .Select(pair => new TypeInitializerConstruction(pair.Key, pair.Value.InstanceId,
                                                                                 pair.Value.Instances.Order(StringComparer.Ordinal).ToArray(),
                                                                                 pair.Value.Regions.Order(StringComparer.Ordinal).ToArray()))
                                 .ToArray(),
                _constructions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                              .Select(pair => new RegionConstruction(pair.Key, pair.Value.ToArray())).ToArray(),
                _counters,
                reachable,
                loweredNotReached,
                _rebuilding.NoReceiver.OrderBy(item => item.BodyId, StringComparer.Ordinal).ThenBy(item => item.OperationId).ToArray(),
                Query((string instanceId, AbstractValue value) => Eval(_instances[instanceId], value)),
                Query<string, string, TrackedSet<string>>(LoadAny),
                Query((string owner, string key) => _cells.GetValueOrDefault((owner, key)) ?? new TrackedSet<string>(StringComparer.Ordinal)),
                _rootInstances,
                Query((string instanceId, IrFieldRef field) => StaticRegion(_instances[instanceId], field)),
                Query((string regionId) => (_fieldsByRegion.GetValueOrDefault(regionId) ?? []).Where(field => _fields[(regionId, field)].Count != 0)
                                                                                              .Order(StringComparer.Ordinal).ToArray()),
                Query((string regionId) => _delegates.ContainsKey(regionId) ? Captures(regionId) : new TrackedSet<string>(StringComparer.Ordinal)))
            {
                ExecutionEdges = executionEdges.OrderBy(edge => edge.CallerInstance, StringComparer.Ordinal).ThenBy(edge => edge.OperationId)
                                              .ThenBy(edge => edge.CalleeInstance, StringComparer.Ordinal).ToArray(),
                IteratorObjects = _iteratorObjects.Select(pair => new IteratorObject(pair.Key, pair.Value)).ToArray(),
                UnknownIterators = unknownIterators,
                IteratorMemberReceivers = _iteratorMemberReceivers.ToTrackedMap(pair => pair.Key,
                                                                                 pair => (IReadOnlySet<string>)pair.Value),
                UnresolvedLocators = unresolved.OrderBy(item => item.BodyId, StringComparer.Ordinal).ThenBy(item => item.OperationId).ToArray(),
                UnresolvedDispatches = _rebuilding.UnresolvedDispatches.ToTrackedSet(),
                UnresolvedDispatchReceivers = _rebuilding.UnresolvedReceivers.ToTrackedMap(pair => pair.Key,
                                                                                           pair => (IReadOnlySet<(string, string?)>)pair.Value.ToTrackedSet()),
                UnresolvedFactoryInputs = _rebuilding.UnresolvedFactoryInputs.ToTrackedMap(pair => pair.Key, pair => (IReadOnlySet<IrFactoryInput>)pair.Value.ToTrackedSet()),
                UnresolvedFateInputs = _rebuilding.UnresolvedFateInputs.ToTrackedMap(pair => pair.Key,
                                                                                     pair => (IReadOnlySet<string>)pair.Value.ToTrackedSet(StringComparer.Ordinal)),
                Holders = _held.Keys.ToTrackedSet(StringComparer.Ordinal),
                LibrarySequences = librarySequences,
                StartupDelegates = _rebuilding.StartupDelegates.OrderBy(item => item.Caller, StringComparer.Ordinal).ThenBy(item => item.Operation)
                                              .ThenBy(item => item.Region, StringComparer.Ordinal).ThenBy(item => item.Callee, StringComparer.Ordinal)
                                              .Select(item => new StartupDelegate(item.Caller, item.Operation, item.Region, item.Callee))
                                              .ToArray(),
                DelegateHandoffs = _rebuilding.Handoffs.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                              .Select(pair => new DelegateHandoff(pair.Key,
                                                                                  pair.Value.Sites.OrderBy(site => site.Caller, StringComparer.Ordinal)
                                                                                      .ThenBy(site => site.Operation).ToArray(),
                                                                                  pair.Value.Callees.Order(StringComparer.Ordinal).ToArray()))
                                              .ToArray(),
                RegionUncertainties = _uncertainties.ToTrackedMap(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray(), StringComparer.Ordinal),
                Collections = _collections.ToTrackedMap(pair => pair.Key,
                                                        pair => (IReadOnlyList<string>)pair.Value.SelectMany(Object).Distinct(StringComparer.Ordinal).ToArray(),
                                                        StringComparer.Ordinal),
                StartupRegions = _startupRegions,
                UnfollowedCallResults = _instances.Values
                                                  .SelectMany(instance => instance.UnfollowedCallResults.Select(operation => (instance.Id, operation)))
                                                  .ToTrackedSet(),
                TaskCompleters = _completers.GroupBy(item => item.Task, StringComparer.Ordinal)
                                            .ToDictionary(group => group.Key, group => (IReadOnlySet<TaskCompleter>)group.Select(item => item.Completer).ToHashSet(),
                                                          StringComparer.Ordinal),
                DelegateReturns = DelegateReturnsExport(),
                FateRuns = _rebuilding.FateRuns.GroupBy(item => item.Callee, StringComparer.Ordinal)
                                      .ToDictionary(group => group.Key, group => (IReadOnlySet<DelegateSite>)group.Select(item => item.Site).ToHashSet(),
                                                    StringComparer.Ordinal),
                UnresolvedCallTargets = _instances.Values
                                                  .SelectMany(instance => instance.UnresolvedCallTargets.Select(operation => (instance.Id, operation)))
                                                  .ToTrackedSet(),
                UnfollowedCompletions = _instances.Values
                                                  .SelectMany(instance => instance.UnfollowedCompletions.Select(operation => (instance.Id, operation)))
                                                  .ToTrackedSet(),
                UnfollowedModelCompletions = _instances.Values
                                                       .SelectMany(instance => instance.UnfollowedModelCompletions.Select(item =>
                                                                       (instance.Id, item.Operation, item.Channel)))
                                                       .ToTrackedSet(),
                CompletionQuery = Query((string task) => (IReadOnlySet<string>)Completion([task])),
                UnfollowedTaskCompletions = _unfollowedCompletions.ToHashSet(StringComparer.Ordinal),
                LocatorCreators = _locatorCreators.ToTrackedMap(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, StringComparer.Ordinal),
                RegionTriggers = _regionTriggers.OrderBy(item => item.InstanceId, StringComparer.Ordinal).ThenBy(item => item.OperationId)
                                                .ThenBy(item => item.RegionId, StringComparer.Ordinal)
                                                .Select(item => new RegionTrigger(item.InstanceId, item.OperationId, item.RegionId)).ToArray(),
                Spawns = Ordered(_spawns).Select(pair => new SpawnSite(pair.Key.Caller, pair.Key.Operation, pair.Value.CallOperationId, pair.Value.Kind!.Value,
                                                                       Sorted(pair.Value.Handles), pair.Value.Tail,
                                                                       pair.Value.Callees.OrderBy(callee => callee.Instance, StringComparer.Ordinal)
                                                                           .ThenBy(callee => callee.Role)
                                                                           .Select(callee => new SpawnCallee(callee.Instance, callee.Role)).ToArray()))
                                         .ToArray(),
                TimerCallbacks = Ordered(_timerCallbacks).Select(pair => new TimerCallbackSite(pair.Key.Caller, pair.Key.Operation, pair.Value.Action!.Value,
                                                                                               Sorted(pair.Value.Handles), Callees(pair.Value))
                {
                    TimersKnown = TimersKnown(pair.Key.Caller, pair.Key.Operation, pair.Value.Handles)
                })
                                                         .ToArray(),
                AsyncSpawns = Ordered(_asyncSpawns).Select(pair => new AsyncSpawnSite(pair.Key.Caller, pair.Key.Operation, pair.Value.Kind!.Value,
                                                                                      pair.Value.Handles.SingleOrDefault(), Callees(pair.Value)))
                                                   .ToArray(),
                Tails = _tails,
                Antecedents = _antecedents.ToTrackedMap(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, StringComparer.Ordinal),
                TaskGroups = _taskGroups.ToTrackedMap(pair => pair.Key, pair => new TaskGroup(pair.Value.Members, pair.Value.Known), StringComparer.Ordinal)
            };

            static IEnumerable<KeyValuePair<(string Caller, int Operation), SiteState>> Ordered(TrackedMap<(string Caller, int Operation), SiteState> sites) =>
                sites.OrderBy(pair => pair.Key.Caller, StringComparer.Ordinal).ThenBy(pair => pair.Key.Operation);

            static IReadOnlySet<string> Sorted(IEnumerable<string> regions) => new SortedSet<string>(regions, StringComparer.Ordinal);

            static IReadOnlyList<string> Callees(SiteState site) =>
                site.Callees.Select(callee => callee.Instance).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }

        /// <summary>What <see cref="ReturnsOf"/> answered at the last pass for each delegate parameter it answered, for the export
        /// (<see cref="HeapSolution.DelegateReturns"/>): the runs, what they give back, and whether an alternative is unseen.</summary>
        private Dictionary<DelegateSite, DelegateReturnRuns> DelegateReturnsExport()
        {
            var runs = _rebuilding.DelegateRuns.ToLookup(item => item.Site, item => item.Run);
            return _rebuilding.DelegateSites.ToDictionary(site => site, site =>
            {
                var siteRuns = runs[site].Distinct().ToArray();
                var regions = siteRuns.SelectMany(run => run.Task is { } task ? [task] : (IEnumerable<string>)_instances[run.Callee].Returns)
                                      .ToHashSet(StringComparer.Ordinal);
                return new DelegateReturnRuns(siteRuns, regions, _rebuilding.UnseenDelegates.Contains(site));
            });
        }

        /// <summary>What a delegate region captures: the receiver it was created on and the values of the variables it closes over.</summary>
        /// <param name="regionId">The regionId.</param>
        private TrackedSet<string> Captures(string regionId)
        {
            var state = _delegates[regionId];
            return state.CapturedReceivers.Concat(state.CellOwners.SelectMany(owner => state.CapturedKeys.SelectMany(key => Cell(owner, key))))
                        .ToTrackedSet(StringComparer.Ordinal);
        }

        /// <summary>The locator calls of every instance that resolve nothing: no constant type, a receiver standing for no scope,
        /// or a service that binds nothing.</summary>
        private TrackedSet<(string BodyId, int OperationId)> UnresolvedLocators()
        {
            var unresolved = new TrackedSet<(string BodyId, int OperationId)>();
            foreach (var instance in _instances.Values)
            {
                CheckCancellation();
                foreach (var call in instance.Summary.OpaqueCalls.Where(call => call.ServiceCall is { Kind: not IrServiceCallKind.ScopeCreation }))
                {
                    CheckCancellation();
                    var service = call.ServiceCall!;
                    var resolves = service.ServiceTypeKey is { } key && Scopes(instance, call).Count != 0 &&
                                   (service.Kind == IrServiceCallKind.Locator
                                       ? key.EndsWith(SERVICE_PROVIDER_TYPE, StringComparison.Ordinal) || _scope.DiIndex.Resolve(key).Kind == DiResolutionKind.Bound
                                       : Supported(_scope.DiIndex.Resolve(key)).Any());
                    if (!resolves)
                        unresolved.Add((instance.BodyId, call.OperationId));
                }
            }

            return unresolved;
        }

        /// <summary>A registration whose object fell back to its own region has an unknown result; one whose object is more than
        /// one region gives each of them the uncertainty that it is more than one object.</summary>
        private void RegistrationUncertainties()
        {
            foreach (var state in _registrations.Values.OrderBy(state => state.Region, StringComparer.Ordinal))
            {
                CheckCancellation();
                var region = _regions[state.Region];
                var source = $"registration at {state.Registration.Source.Path}:{state.Registration.Source.StartLine}";
                if (state.Defaulted && Object(state.Region).Count == 1)
                {
                    Uncertain(state.Region, $"{region.Display}: factory result is unknown ({source}).");
                    continue;
                }

                var objects = Object(state.Region);
                if (objects.Count > 1)
                {
                    foreach (var target in objects.Order(StringComparer.Ordinal))
                    {
                        CheckCancellation();
                        Uncertain(target, $"{region.Display}: factory returns more than one object ({source}).");
                    }
                }
            }
        }

        private void Uncertain(string region, string uncertainty)
        {
            if (!_uncertainties.TryGetValue(region, out var list))
                _uncertainties.Add(region, list = []);
            if (!list.Contains(uncertainty))
                list.Add(uncertainty);
        }

        /// <summary>Runs the entry point and every member holding a factory or instance registration as the container's startup
        /// construction. A member another startup member reaches through its call chain is instantiated by those calls; any other (and
        /// every member of a call cycle no other member reaches) has its service collection bound to the container and every other
        /// parameter to a symbolic region of an unknown caller.</summary>
        private void SeedStartup()
        {
            CheckCancellation();
            var members = ReachableSet.StartupMembers(_program, _scope.DiIndex).Where(_scope.Reachable.Bodies.ContainsKey).ToArray();
            if (members.Length == 0)
                return;

            var container = Region($"container|{_scope.ScopeId}", HeapRegionKind.Container, "container", null, STARTUP_CONTEXT, null);
            _startupRegions.Add(container);
            var callees = members.ToTrackedMap(member => member, Callees, StringComparer.Ordinal);
            var seeds = members.Where(member => !members.Any(other => other != member && callees[other].Contains(member))).ToTrackedSet(StringComparer.Ordinal);
            var covered = seeds.SelectMany(seed => callees[seed].Append(seed)).ToTrackedSet(StringComparer.Ordinal);
            seeds.UnionWith(members.Where(member => !covered.Contains(member)));
            var instances = new TrackedList<string>();
            _constructions.Add(container, instances);
            foreach (var member in members)
            {
                CheckCancellation();
                if (!seeds.Contains(member) || _program.Method(member) is not { } method)
                    continue;
                var instance = Instance(member, STARTUP_CONTEXT, NO_SUBSTITUTION, [], [], !method.IsStatic);
                if (instance is null)
                    continue;
                instances.Add(instance.Id);
                for (var ordinal = 0; ordinal < method.Parameters.Count; ordinal++)
                {
                    CheckCancellation();
                    var parameter = method.Parameters[ordinal];
                    if (parameter.TypeKey.EndsWith(SERVICE_COLLECTION_TYPE, StringComparison.Ordinal))
                    {
                        Add(Parameter(instance, ordinal), [container]);
                        continue;
                    }

                    var display = $"param:{method.DisplaySymbol}#{parameter.Name}";
                    var symbolic = Region($"param|{member}|{ordinal}", HeapRegionKind.Symbolic, display, parameter.TypeKey, STARTUP_CONTEXT, null);
                    Uncertain(symbolic, $"{display}: value comes from an unknown caller of {method.DisplaySymbol}.");
                    Add(Parameter(instance, ordinal), [symbolic]);
                }
            }
        }

        private const string STARTUP_CONTEXT = "startup";

        private IEnumerable<string> BodiesOf(string member) => [member, .. _program.Method(member)?.NestedBodyIds ?? []];

        /// <summary>Every reachable member or local function a member's calls reach, transitively.</summary>
        /// <param name="member">The member.</param>
        private TrackedSet<string> Callees(string member)
        {
            CheckCancellation();
            var reached = new TrackedSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>([member]);
            while (pending.TryDequeue(out var current))
            {
                CheckCancellation();
                foreach (var call in BodiesOf(current).SelectMany(body => _scope.Summaries.Get(body)?.Calls ?? []))
                {
                    CheckCancellation();
                    if (_scope.Reachable.Bodies.ContainsKey(call.Target) && reached.Add(call.Target))
                        pending.Enqueue(call.Target);
                }
            }

            return reached;
        }

        private void Seed()
        {
            CheckCancellation();
            SeedStartup();
            foreach (var root in _scope.Roots.OrderBy(root => root.StableRootId, StringComparer.Ordinal))
            {
                CheckCancellation();
                var bindings = root.InstanceBindings;
                var invocation = $"root:{root.StableRootId}";
                var context = invocation;
                string? receiver = null;
                ResolutionScope scope = new(root.StableRootId);
                if (bindings is { Receiver: ReceiverKind.HostedService, ReceiverTypeKey: { } hostedKey })
                {
                    scope = new ResolutionScope(null);
                    var hosted = _scope.DiIndex.HostedServices.FirstOrDefault(candidate => candidate.ImplementationTypeKey == hostedKey);
                    receiver = Region($"{HeapIdentity(DiIndex.HOSTED_SERVICE_KEY, hostedKey, DiLifetime.Singleton, 1)}|singleton", HeapRegionKind.Di,
                                      DiIndex.RegionDisplay(bindings.ReceiverType ?? DisplayType(hostedKey), DiLifetime.Singleton, 1), hostedKey,
                                      "singleton", null, mayOverlapItself: hosted?.InstanceCount == HostedServiceInstanceCount.Unknown);
                    Construct(receiver, hostedKey, scope);
                }
                else if (bindings is { Receiver: ReceiverKind.PerInvocation, ReceiverTypeKey: { } controllerKey })
                {
                    receiver = Region($"receiver|{controllerKey}|{invocation}", HeapRegionKind.Receiver, $"receiver:{DisplayType(controllerKey)}",
                                      controllerKey, invocation, null);
                    Construct(receiver, controllerKey, scope);
                }
                else if (bindings is { Receiver: ReceiverKind.DiService, ReceiverTypeKey: { } serviceKey })
                {
                    receiver = Resolve(_scope.DiIndex.Resolve(serviceKey), scope, invocation, root.Entry.Symbol, "this").FirstOrDefault();
                }

                var bodyId = root.Entry.BodyKey;
                var memberId = _memberOfNestedBody.GetValueOrDefault(bodyId);
                var method = _program.Method(memberId ?? bodyId);
                var substitution = receiver is not null && method is not null ? ReceiverSubstitution(_regions[receiver], method, []) : NO_SUBSTITUTION;
                var entry = Instance(bodyId, receiver ?? context, substitution, receiver is null ? [] : [receiver],
                                     memberId is null ? [] : [invocation], receiver is null && method is { IsStatic: false });
                if (entry is null)
                    continue;
                _rootInstances[root.StableRootId] = entry.Id;
                if (receiver is not null && scope.InvocationRootId is not null)
                    _regionTriggers.Add((entry.Id, -1, receiver));
                if (scope.InvocationRootId is { } request)
                    Add(entry.Requests, [request]);

                for (var ordinal = 0; ordinal < bindings.Parameters.Count; ordinal++)
                {
                    CheckCancellation();
                    var parameter = bindings.Parameters[ordinal];
                    if (IsServiceProvider(parameter.TypeKey) || parameter.Type is "System.IServiceProvider" or "IServiceProvider")
                    {
                        Add(Parameter(entry, ordinal), [Provider(scope)]);
                    }
                    else if (parameter is { Kind: ParameterBindingKind.DiService, IsValueType: false, TypeKey: { } typeKey })
                    {
                        var resolved = Resolve(_scope.DiIndex.Resolve(typeKey), scope, invocation, root.Entry.Symbol, parameter.Name);
                        Inject(Parameter(entry, ordinal), resolved);
                        Trigger(entry, -1, resolved);
                    }
                }
            }
        }

        private static bool IsServiceProvider(string? typeKey) => typeKey?.EndsWith(SERVICE_PROVIDER_TYPE, StringComparison.Ordinal) == true;

        /// <summary>The region standing for a scope's <see cref="IServiceProvider"/>.</summary>
        /// <param name="scope">The resolution scope.</param>
        private string Provider(ResolutionScope scope)
        {
            var provider = Region($"provider|{scope.Context}", HeapRegionKind.Provider, $"provider:{scope.Context}", null, scope.Context, null);
            _providers.TryAdd(provider, scope);
            return provider;
        }

        /// <summary>Binds a target to the objects of the resolved regions, now and in every later round, since a factory or instance
        /// registration's object grows with the fixpoint.</summary>
        /// <param name="target">The target.</param>
        /// <param name="regions">The regions.</param>
        private void Inject(TrackedSet<string> target, IEnumerable<string> regions)
        {
            CheckCancellation();
            foreach (var region in regions)
            {
                CheckCancellation();
                _sinks.Add((target, region));
                Add(target, Object(region));
            }
        }

        /// <summary>The objects a resolved region stands for: the region itself, or a factory or instance registration's object.</summary>
        /// <param name="region">The region.</param>
        private IReadOnlyCollection<string> Object(string region)
        {
            CheckCancellation();
            if (!_registrations.TryGetValue(region, out var state))
                return [region];

            var objects = new TrackedSet<string>(StringComparer.Ordinal);
            if (state.Registration.Form == DiRegistrationForm.Factory)
            {
                foreach (var instance in state.Instances)
                {
                    CheckCancellation();
                    objects.UnionWith(_instances[instance].Returns);
                }
            }
            else if (Site(state.Registration) is { } site && LastArgument(site.Call) is { } argument)
            {
                foreach (var holder in _instances.Values.Where(instance => instance.BodyId == site.BodyId).ToArray())
                {
                    CheckCancellation();
                    objects.UnionWith(Eval(holder, argument.Values));
                }
            }

            if (state.Defaulted)
                objects.Add(region);
            return objects;
        }

        private static CallArgument? LastArgument(SummaryOpaqueCall call) => call.Arguments.OrderBy(argument => argument.ParameterOrdinal).LastOrDefault();

        /// <summary>The body and opaque call of a registration's recorded call.</summary>
        /// <param name="registration">The registration.</param>
        private (string BodyId, SummaryOpaqueCall Call)? Site(DiRegistration registration)
        {
            var key = (registration.BodyId!, registration.OperationId!.Value);
            if (_sites.TryGetValue(key, out var site))
                return site;

            site = null;
            foreach (var body in BodiesOf(registration.BodyId!).Where(_scope.Reachable.Bodies.ContainsKey))
            {
                CheckCancellation();
                if (_scope.Summaries.Get(body)?.OpaqueCalls.FirstOrDefault(call => call.OperationId == key.Item2 &&
                                                                                   call.Callee.Contains(registration.Method, StringComparison.Ordinal))
                        is { } call)
                {
                    site = (body, call);
                    break;
                }
            }

            _sites.Add(key, site);
            return site;
        }

        /// <summary>Runs a reached factory registration's delegate targets as the construction of its region: a nested body with the
        /// creating instance's cells, a static method, or an instance method on each captured receiver, each in the region's context,
        /// with the provider parameter standing for the triggering scope.</summary>
        /// <param name="state">The state.</param>
        private void ConstructFactory(RegistrationState state)
        {
            CheckCancellation();
            if (state.Registration.Form != DiRegistrationForm.Factory || Site(state.Registration) is not { } site || LastArgument(site.Call) is not { } argument)
                return;

            var delegates = _instances.Values.Where(instance => instance.BodyId == site.BodyId).ToArray()
                                      .SelectMany(holder => Eval(holder, argument.Values))
                                      .Where(_delegates.ContainsKey)
                                      .Distinct(StringComparer.Ordinal)
                                      .ToArray();
            foreach (var region in delegates)
            {
                CheckCancellation();
                var target = _delegates[region];
                foreach (var instance in FactoryInstances(state.Region, target))
                {
                    CheckCancellation();
                    _factoryInstances.TryAdd(instance.Id, state.Region);
                    if (state.Instances.Add(instance.Id))
                        _changes++;
                    if (!_constructions.TryGetValue(state.Region, out var constructors))
                        _constructions.Add(state.Region, constructors = []);
                    if (!constructors.Contains(instance.Id))
                        constructors.Add(instance.Id);
                    Add(Parameter(instance, 0), [Provider(state.Scope)]);
                    if (state.Scope.InvocationRootId is { } request)
                        Add(instance.Requests, [request]);
                }
            }
        }

        private IEnumerable<InstanceState> FactoryInstances(string region, DelegateState target)
        {
            if (target.IsNestedBody)
            {
                var owner = target.CellOwners.Select(id => _instances.GetValueOrDefault(id)).OfType<InstanceState>().FirstOrDefault();
                if (Instance(target.Target, region, target.OwnerSubstitution, target.CapturedReceivers, target.CellOwners, owner?.IsReceiverless ?? false)
                    is { } nested)
                {
                    yield return nested;
                }

                yield break;
            }

            if (_program.Method(target.Target) is not { } method)
                yield break;
            if (method.IsStatic)
            {
                if (Instance(method.MethodId, region, Substitution(method, target.ContainingTypeKey, target.MethodTypeArguments), [], [], false) is { } factory)
                    yield return factory;
                yield break;
            }

            foreach (var receiver in target.CapturedReceivers.ToArray())
            {
                CheckCancellation();
                var implementation = _regions[receiver].TypeKey is { } typeKey && _program.Implementation(typeKey, method.MethodId, target.ContainingTypeKey) is { HasSourceBody: true } found
                    ? found
                    : method;
                if (Instance(implementation.MethodId, $"{region}|{receiver}", ReceiverSubstitution(_regions[receiver], implementation, target.MethodTypeArguments),
                             [receiver], [], false) is { } factory)
                {
                    yield return factory;
                }
            }
        }

        /// <summary>The region a DI resolution gives: the bound registration's region, or the scope's provider for
        /// <see cref="IServiceProvider"/>.</summary>
        /// <param name="resolution">The DI resolution.</param>
        /// <param name="scope">The scope the resolution happens in.</param>
        /// <param name="resolver">What resolves the service: the invocation, the resolving region or the resolving instance; a transient's
        /// context.</param>
        /// <param name="consumer">What consumes the service; a transient's context.</param>
        /// <param name="parameter">The parameter the service is resolved for, or empty; a transient's context.</param>
        private IReadOnlyList<string> Resolve(DiResolution resolution, ResolutionScope scope, string resolver, string consumer, string parameter)
        {
            if (IsServiceProvider(resolution.ServiceType))
                return [Provider(scope)];
            if (resolution is not { Kind: DiResolutionKind.Bound, Binding: { } binding })
                return [];

            var registration = resolution.Registrations.LastOrDefault(candidate =>
                                   DiIndex.RegionId(resolution.ServiceType, candidate.ImplementationTypeKey!, candidate.Lifetime, candidate.Number) == binding.RegionId)
                               ?? resolution.Registrations[^1];
            return [Registration(resolution.ServiceType, registration, scope, resolver, consumer, parameter)];
        }

        /// <summary>A registration's identity in the heap: its index identity, the number shown only from the second identical
        /// registration on, as the display shows it.</summary>
        /// <param name="serviceTypeKey">The service type.</param>
        /// <param name="implementationTypeKey">The implementation type.</param>
        /// <param name="lifetime">The registration's lifetime.</param>
        /// <param name="number">The registration's number among identical registrations.</param>
        private static string HeapIdentity(string serviceTypeKey, string implementationTypeKey, DiLifetime lifetime, int number) =>
            number < 2 ? $"di|{serviceTypeKey}|{implementationTypeKey}@{lifetime}" : DiIndex.RegionId(serviceTypeKey, implementationTypeKey, lifetime, number);

        private static IEnumerable<DiRegistration> Supported(DiResolution resolution) =>
            resolution.Registrations.Where(registration => registration is { IsSupported: true, IsHostedService: false, ImplementationTypeKey: not null });

        /// <summary>The region of one registration: one object for a singleton; per invocation, per created scope, or one root-scope
        /// object, for a scoped service; per resolving object, consumer and parameter for a transient. A new region with a type
        /// registration is constructed; a factory or instance registration's region is recorded for its object.</summary>
        /// <param name="serviceTypeKey">The service type.</param>
        /// <param name="registration">The registration.</param>
        /// <param name="scope">The scope the resolution happens in.</param>
        /// <param name="resolver">What resolves the service; a transient's context.</param>
        /// <param name="consumer">What consumes the service; a transient's context.</param>
        /// <param name="parameter">The parameter the service is resolved for, or empty; a transient's context.</param>
        private string Registration(string serviceTypeKey, DiRegistration registration, ResolutionScope scope, string resolver, string consumer,
                                    string parameter)
        {
            var context = registration.Lifetime switch
            {
                DiLifetime.Singleton => "singleton",
                DiLifetime.Scoped => scope.Context,
                _ => $"{resolver}|{consumer}|{parameter}"
            };
            var identity = $"{HeapIdentity(serviceTypeKey, registration.ImplementationTypeKey!, registration.Lifetime, registration.Number)}|{context}";
            if (_regions.ContainsKey(identity))
                return identity;

            Region(identity, HeapRegionKind.Di, DiIndex.RegionDisplay(registration.ImplementationType!, registration.Lifetime, registration.Number),
                   registration.ImplementationTypeKey, context, null);
            var constructionScope = registration.Lifetime == DiLifetime.Singleton ? new ResolutionScope(null) : scope;
            if (registration.Form == DiRegistrationForm.Type)
            {
                Construct(identity, registration.ImplementationTypeKey!, constructionScope);
            }
            else
            {
                _registrations.Add(identity, new RegistrationState(identity, registration, constructionScope));
                if (registration.Form == DiRegistrationForm.Instance)
                    _startupRegions.Add(identity);
            }

            return identity;
        }

        /// <summary>Runs a region's selected constructor with the region as <c>this</c> and its parameters bound to their resolved DI
        /// regions, once, and activates the built type's type initializer.</summary>
        /// <param name="regionId">The regionId.</param>
        /// <param name="typeKey">The typeKey.</param>
        /// <param name="scope">The scope.</param>
        private void Construct(string regionId, string typeKey, ResolutionScope scope)
        {
            CheckCancellation();
            if (_constructions.ContainsKey(regionId))
                return;
            var constructorInstances = new TrackedList<string>();
            _constructions.Add(regionId, constructorInstances);

            var type = _program.Type(typeKey);
            var bindings = type is null ? null : _scope.InjectionBindings.FirstOrDefault(candidate => candidate.TypeKey == type.TypeKey);
            foreach (var constructor in ReachableSet.SelectedConstructors(_program, typeKey, bindings))
            {
                CheckCancellation();
                var instance = Instance(constructor.MethodId, regionId, ReceiverSubstitution(_regions[regionId], constructor, []), [regionId], [], false);
                if (instance is null)
                    continue;
                constructorInstances.Add(instance.Id);
                if (scope.InvocationRootId is { } request)
                    Add(instance.Requests, [request]);
                foreach (var parameter in bindings?.ConstructorParameters ?? [])
                {
                    CheckCancellation();
                    if (parameter.Ordinal < constructor.Parameters.Count && constructor.Parameters[parameter.Ordinal].TypeKey == parameter.TypeKey)
                    {
                        var resolution = ReachableSet.ResolveConstructorParameter(_program, _scope.DiIndex, typeKey, parameter);
                        var resolved = Resolve(resolution, scope, regionId, typeKey, parameter.Name);
                        Inject(Parameter(instance, parameter.Ordinal), resolved);
                        Trigger(instance, -1, resolved);
                        // A created scope's objects have no execution of their own: their constructions run where the scope's owner runs.
                        foreach (var child in resolved.Where(child => child.Contains("|scope:", StringComparison.Ordinal)))
                        {
                            CheckCancellation();
                            ConstructionEdges(instance, -1, child);
                        }
                    }
                }
            }

            ActivateTypeInitializer(typeKey, null, regionId);
        }

        private InstanceState? Instance(string bodyId, string context, IReadOnlyDictionary<string, string> substitution,
                                        IEnumerable<string> receivers, IEnumerable<string> cellOwners, bool receiverless,
                                        bool merged = false)
        {
            if (!_scope.Reachable.Bodies.ContainsKey(bodyId))
                return null;

            merged = merged || _mergedBodies.Contains(bodyId) || substitution.Values.Any(_program.IsOpen);
            var key = $"{bodyId}@{context}{SubstitutionText(substitution)}";
            if (!merged && !_instances.ContainsKey(key))
            {
                var count = _contextCounts.GetValueOrDefault(bodyId);
                if (count >= _limits.MaxContextsPerMethod)
                {
                    _mergedBodies.Add(bodyId);
                    merged = true;
                }
                else
                {
                    _contextCounts[bodyId] = count + 1;
                }
            }

            if (merged)
                key = $"{bodyId}@merged";

            if (!_instances.TryGetValue(key, out var instance))
            {
                var summary = _scope.Summaries.Get(bodyId);
                if (summary is null)
                    return null;
                instance = new InstanceState(key, bodyId, merged ? "merged" : context, merged ? NO_SUBSTITUTION : substitution, merged,
                                             !merged && receiverless, summary);
                _instances.Add(key, instance);
                _instanceOrder.Add(key);
                _changes++;
                if (merged)
                    _counters[HeapCounters.MERGED_CONTEXT] = _counters.GetValueOrDefault(HeapCounters.MERGED_CONTEXT) + 1;
                if (!_memberOfNestedBody.ContainsKey(bodyId))
                    instance.CellOwners.Add(key);
                if (_instanceRegistrationsByMember.TryGetValue(_memberOfNestedBody.GetValueOrDefault(bodyId) ?? bodyId, out var registrations))
                    MapRegisteredAllocations(instance, registrations);
            }

            Add(instance.Receivers, receivers);
            Add(instance.CellOwners, cellOwners);
            return instance;
        }

        /// <summary>The objects an instance registration's argument allocates in the registration body are the registration's own
        /// region, constructed there at startup.</summary>
        /// <param name="instance">The instance.</param>
        /// <param name="registrations">The registrations.</param>
        private void MapRegisteredAllocations(InstanceState instance, IReadOnlyList<DiRegistration> registrations)
        {
            foreach (var registration in registrations)
            {
                CheckCancellation();
                if (Site(registration) is not { } site || site.BodyId != instance.BodyId || LastArgument(site.Call) is not { } argument)
                    continue;
                var region = Registration(registration.ServiceTypeKey!, registration, new ResolutionScope(null), "", "", "");
                foreach (var allocation in argument.Values.OfType<AllocationValue>())
                {
                    CheckCancellation();
                    _registeredAllocations.TryAdd((allocation.Site.BodyId, allocation.Site.OperationId, ContextKey(instance)), region);
                }
            }
        }

        private static string SubstitutionText(IReadOnlyDictionary<string, string> substitution) =>
            substitution.Count == 0 ? "" : "|" + string.Join(";", substitution.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                                                              .Select(pair => $"{pair.Key}={pair.Value}"));

        private void Process(InstanceState instance)
        {
            CheckCancellation();
            _counters[HeapCounters.INSTANCE_PROCESSINGS]++;
            var summary = instance.Summary;
            var memberId = _memberOfNestedBody.GetValueOrDefault(instance.BodyId) ?? instance.BodyId;
            var capturedKeys = CapturedKeys(memberId);
            foreach (var variable in summary.Variables.Where(variable => capturedKeys.Contains(variable.SymbolKey)))
            {
                CheckCancellation();
                var values = Eval(instance, variable.Values);
                foreach (var owner in instance.CellOwners.ToArray())
                {
                    CheckCancellation();
                    Add(Cell(owner, variable.SymbolKey), values);
                }
            }

            foreach (var store in summary.CapturedStores)
            {
                CheckCancellation();
                var values = Eval(instance, store.Values);
                foreach (var owner in instance.CellOwners.ToArray())
                {
                    CheckCancellation();
                    Add(Cell(owner, store.SymbolKey), values);
                }
            }

            foreach (var store in summary.Stores)
            {
                CheckCancellation();
                var values = Eval(instance, store.Values);
                if (store.Field.IsStatic)
                    Add(Field(StaticRegion(instance, store.Field), FieldSlot.Key(store.Field)), values);
                else
                    foreach (var @base in Eval(instance, store.Bases))
                    {
                        CheckCancellation();
                        Add(Field(@base, FieldSlot.Key(store.Field)), values);
                    }
            }

            foreach (var element in summary.Elements.Where(element => element.Kind == ElementOperationKind.Store))
            {
                CheckCancellation();
                // What an interface call does goes only into, and only comes from, the objects of the kinds whose member it is there, and
                // never into an object whose type of the run's own implements the member itself (ADR 0010, amendment of the phase 5b
                // third run).
                bool Decides(string region, IReadOnlySet<string>? kinds) =>
                    kinds is null || ObjectKind(region, element.InterfaceMethod) is { } kind && kinds.Contains(kind);

                // A view an interface call makes holds what the storage it views holds in each object it is made of.
                if (element.ViewOf is { } viewed)
                {
                    var viewedHeld = Eval(instance, element.CopiedFrom!).Where(source => Decides(source, element.ValueKinds))
                                                                         .SelectMany(source => Load(source, viewed))
                                                                         .ToTrackedSet(StringComparer.Ordinal);
                    foreach (var view in Eval(instance, element.Arrays))
                    {
                        CheckCancellation();
                        Add(Field(view, element.Slot), viewedHeld);
                    }
                    continue;
                }

                if (element.CopiedFrom is not null)
                {
                    Copy(instance, element);
                    continue;
                }

                // What an interface call hands out of an object it decides is what that object's own storage holds.
                if (element.FromSlot is { } from)
                {
                    foreach (var array in Eval(instance, element.Arrays).Where(array => Decides(array, element.ArrayKinds) &&
                                                                                        !(element.ExceptKinds is { } except &&
                                                                                          ObjectKind(array, element.InterfaceMethod) is { } kind &&
                                                                                          except.Contains(kind))))
                    {
                        CheckCancellation();
                        Add(Field(array, element.Slot), Load(array, from));
                    }
                    continue;
                }

                var values = Eval(instance, element.Values);
                if (element.ValueKinds is { } valueKinds)
                    values = values.Where(region => Decides(region, valueKinds)).ToTrackedSet(StringComparer.Ordinal);
                foreach (var array in Eval(instance, element.Arrays).Where(array => Decides(array, element.ArrayKinds)))
                {
                    CheckCancellation();
                    Add(Field(array, element.Slot), values);
                }
            }

            foreach (var returned in summary.ReferenceReturns)
                AddLocations(instance.ReturnedLocations, ReferenceLocations(instance, returned));

            Add(instance.Yields, Eval(instance, summary.Yields));
            foreach (var @return in summary.Returns)
            {
                CheckCancellation();
                Add(instance.Returns, Eval(instance, @return.Values));
                if (!instance.ReturnsUnfollowed && Unfollowed(instance, @return.UnknownSources, @return.SourceCalls, @return.Completions))
                {
                    instance.ReturnsUnfollowed = true;
                    _changes++;
                }
            }
            foreach (var parameter in summary.RefParameters)
            {
                CheckCancellation();
                Add(RefParameter(instance, parameter.Ordinal), Eval(instance, parameter.Values));
            }

            foreach (var created in summary.Delegates)
            {
                CheckCancellation();
                var region = DelegateRegion(instance, created.Delegate);
                var state = _delegates[region];
                Add(state.CellOwners, instance.CellOwners);
                state.CapturedKeys.UnionWith(created.Delegate.CapturedValues.Keys.Where(key => key != "this"));
                if (created.Delegate.CapturedValues.TryGetValue("this", out var thisValues))
                    Add(state.CapturedReceivers, Eval(instance, thisValues));
                if (!state.IsNestedBody && _program.Method(state.Target) is { IsStatic: true } method)
                    ActivateTypeInitializer(state.ContainingTypeKey ?? method.ContainingTypeKey, instance, null);
            }

            foreach (var access in summary.Accesses.Where(access => access.Field.IsStatic))
            {
                CheckCancellation();
                StaticRegion(instance, access.Field);
                ActivateTypeInitializer(StaticTypeKey(instance, access.Field), instance, null);
            }

            foreach (var call in summary.Calls)
            {
                CheckCancellation();
                Call(instance, call);
            }
            foreach (var call in summary.OpaqueCalls.Where(call => IteratorMemberOf(call.Callee) != IrEnumerationRole.None))
            {
                CheckCancellation();
                IteratorMember(instance, call.OperationId, Eval(instance, call.Receivers));
            }
            foreach (var call in summary.OpaqueCalls)
            {
                CheckCancellation();
                Locate(instance, call);
            }
            foreach (var call in summary.OpaqueCalls.Where(call => InterproceduralAccesses.RunsFactories(call.Collection)))
            {
                CheckCancellation();
                RunFactories(instance, call);
            }
            foreach (var call in summary.OpaqueCalls.Where(call => call is { IsKnown: true, Library: { Fates.Count: > 0 } or { Result: not null } or { Stores.Count: > 0 } or { Outputs.Count: > 0 } or { Keeps.Count: > 0 } }))
            {
                CheckCancellation();
                RunFates(instance, call);
            }

            foreach (var call in summary.OpaqueCalls
                                        .SelectMany(call => CollectionObjects.LibraryCalls(call, value => Eval(instance, value), region => ObjectKind(region)))
                                        .Concat(summary.Calls.SelectMany(call => CollectionObjects.LibraryCalls(call, value => Eval(instance, value),
                                                                          region => ObjectKind(region, call.Target))))
                                        .Where(call => call.IsKnown))
            {
                CheckCancellation();
                RunFates(instance, call);
                foreach (var store in call.Library!.Stores)
                {
                    CheckCancellation();
                    foreach (var target in Eval(instance, ArgumentOf(call, store.Key)))
                    {
                        CheckCancellation();
                        Add(Field(target, PathValue.ELEMENT), _libraryStored.GetValueOrDefault((instance.Id, call.OperationId, store.Key)) ?? []);
                    }
                }
            }

            // A member of a holder called on it, with no body of its own, runs what the holder keeps; a holder reachable from what a call
            // without a body is handed is one the heap cannot follow any more (R4).
            if (!_held.IsEmpty)
            {
                foreach (var call in summary.OpaqueCalls)
                {
                    CheckCancellation();
                    if (!call.IsConstructor)
                    {
                        foreach (var holder in Eval(instance, call.Receivers).Where(_held.ContainsKey).ToArray())
                        {
                            CheckCancellation();
                            RunHeld(instance, call.OperationId, call.Callee, call.Arguments, holder);
                        }
                    }

                    // A delegate a known call's model gives a fate goes where its fate says, which runs it in an unknown execution only
                    // where the fate does; what it captures escapes there (EscapeCapturedHolders), not at the call.
                    var fated = call.IsKnown ? call.Library!.Fates.Select(fate => fate.ParameterOrdinal).ToTrackedSet() : [];
                    EscapeHolders(instance, call.OperationId, call.Arguments.Where(argument => !fated.Contains(argument.ParameterOrdinal))
                                                                           .SelectMany(argument => argument.Values));
                }
            }

            // A grouping answers `Key` with what its model says the key is (R5).
            foreach (var call in summary.OpaqueCalls.Where(call => call.IsGroupingKey && !_groupings.IsEmpty))
            {
                CheckCancellation();
                Add(CallResult(instance, call.OperationId),
                    Eval(instance, call.Receivers).Where(_groupings.Contains).SelectMany(grouping => Load(grouping, PathValue.KEYS)));
            }

            // What a consumer enumerates it enumerates where it stands: a `foreach`, a copy of ADR 0010, a known call reading it deep or
            // naming its elements (R5).
            if (!_sequences.IsEmpty || !_iteratorObjects.IsEmpty)
            {
                foreach (var effect in summary.ArgumentEffects.Where(effect => effect is { IsDeferred: false, Member: null, Kind: IrLibraryEffectKind.Enumerate or IrLibraryEffectKind.DeepRead }))
                {
                    CheckCancellation();
                    Consume(instance, effect.OperationId, Eval(instance, effect.Values), new TrackedSet<string>(StringComparer.Ordinal));
                }
            }

            foreach (var threadWork in summary.ThreadWorks)
            {
                CheckCancellation();
                var work = Eval(instance, threadWork.Work.Values);
                foreach (var thread in Eval(instance, threadWork.Thread.Values))
                {
                    CheckCancellation();
                    Add(Field(thread, THREAD_WORK_FIELD), work);
                }
            }

            foreach (var spawn in summary.Spawns)
            {
                CheckCancellation();
                Spawn(instance, spawn);
            }
            foreach (var timer in summary.Timers.Where(timer => timer.Callback is not null))
            {
                CheckCancellation();
                TimerCallback(instance, timer);
            }
            // What an unresolved call is handed it may run whenever it likes (R3); a call a recognizer or the table models is no such call.
            var modelled = Modelled ??= new UnknownCalls.Modelled(_scope);
            foreach (var call in summary.OpaqueCalls.Where(call => !call.IsKnown && !modelled.Contains(instance.BodyId, call) && !DecidesEvery(instance, call)))
            {
                CheckCancellation();
                Handoff(instance, call.OperationId, call.Arguments.SelectMany(argument => argument.Values).Concat(call.Delegates));
            }
            foreach (var dynamic in summary.DynamicOperations)
            {
                CheckCancellation();
                Handoff(instance, dynamic.OperationId, dynamic.Values);
            }
            foreach (var whenAll in summary.WhenAlls)
            {
                CheckCancellation();
                WhenAll(instance, whenAll);
            }
            foreach (var unwrap in summary.Unwraps)
            {
                CheckCancellation();
                var outer = Eval(instance, unwrap.Outer.Values);
                Add(CallResult(instance, unwrap.CallOperationId), Completion(outer));
                ReadsCompletion(instance, unwrap.CallOperationId, outer);
            }
            foreach (var task in summary.TaskOperations)
            {
                CheckCancellation();
                TaskOperation(instance, task);
            }
            foreach (var join in summary.Joins.Where(join => join.Kind is SummaryJoinKind.Await or SummaryJoinKind.Result))
            {
                CheckCancellation();
                ReadsCompletion(instance, join.OperationId, Eval(instance, join.Handles.SelectMany(handle => handle.Values)));
            }
        }

        /// <summary>Records that an operation reading the completion of tasks — an await, a <c>Result</c> join, an <c>Unwrap()</c> — may give an object the heap
        /// does not follow, when any of the tasks may complete with one (<see cref="Unfollowed"/>).</summary>
        /// <param name="instance">The instance.</param>
        /// <param name="operationId">The reading operation.</param>
        /// <param name="tasks">The tasks it reads.</param>
        private void ReadsCompletion(InstanceState instance, int operationId, IEnumerable<string> tasks)
        {
            if (!instance.UnfollowedCompletions.Contains(operationId) && tasks.Any(_unfollowedCompletions.Contains) &&
                instance.UnfollowedCompletions.Add(operationId))
            {
                _changes++;
            }
        }

        /// <summary>Runs a spawn's work: the delegates it names, the work bound to a started thread, or the <c>Execute()</c> of each
        /// work item object. A handle is a region of the site in the caller's context; <c>StartNew</c>, <c>ContinueWith</c> and a
        /// <c>Task.Run</c> overload that does not wait for its work's task also have a tail once a resolved callee's body is async. The work gets its state, a continuation its
        /// antecedent, and a <c>Parallel</c> loop's body and <c>localFinally</c> the values <c>localInit</c> and the body return. A task's
        /// handle completes with what one run of each work completes with where the overload waits for the work's task, and otherwise with
        /// what the work returns: an async work's tail, which completes with its returns, or a non-async work's returns themselves.</summary>
        /// <param name="caller">The caller.</param>
        /// <param name="spawn">The spawn.</param>
        private void Spawn(InstanceState caller, SummarySpawn spawn)
        {
            var site = SiteOf(_spawns, caller, spawn.OperationId, () => new SiteState(spawn.Kind, spawn.CallOperationId));
            IReadOnlyList<TrackedSet<string>> works;
            string? handle = null;
            if (spawn.Kind == IrSpawnKind.ThreadStart)
            {
                var threads = Eval(caller, spawn.Handle!.Values);
                Add(site.Handles, threads);
                works = [threads.SelectMany(thread => Load(thread, THREAD_WORK_FIELD)).ToTrackedSet(StringComparer.Ordinal)];
            }
            else
            {
                works = spawn.Work.Select(work => Eval(caller, work.Values)).ToArray();
                if (spawn.Handle is not null)
                {
                    handle = TaskRegion(caller, spawn.CallOperationId);
                    Add(site.Handles, [handle]);
                    Add(CallResult(caller, spawn.CallOperationId), [handle]);
                    if (spawn.Antecedent is { } antecedent)
                        Add(Get(_antecedents, handle), Eval(caller, antecedent.Values));
                }
            }

            // A work value the heap cannot follow, a delegate that runs no body it has, or a work that resolves no callee is code the
            // analysis does not see in full: what the spawn's task completes with is then unknown as well.
            var unseen = false;
            var callees = works.Select(regions => spawn.WorkMethod is { } workMethod
                                                      ? WorkItemCallees(regions, workMethod)
                                                      : regions.SelectMany(WorkCallees).ToTrackedList())
                               .ToArray();
            if (handle is not null && site.Tail is null &&
                (spawn.Kind is IrSpawnKind.StartNew or IrSpawnKind.ContinueWith || spawn is { Kind: IrSpawnKind.TaskRun, AwaitsWorkTask: false }) &&
                callees.SelectMany(list => list).Any(callee => IsAsyncBody(callee.BodyId)))
            {
                site.Tail = TailRegion(handle);
                _changes++;
            }

            if (handle is not null && spawn.Kind is IrSpawnKind.TaskRun or IrSpawnKind.StartNew or IrSpawnKind.ContinueWith)
            {
                // The export tells a work whose body is not seen apart from a work value it reads itself (TaskCompleterKind.Spawn).
                var unseenCode = unseen || callees.Any(list => list.Count == 0);
                unseen = unseenCode || spawn.Work.Any(work => Unfollowed(caller, work.UnknownSources, work.SourceCalls, work.Completions));
                Complete(handle, [], unseen, new TaskCompleter(caller.Id, spawn.OperationId, TaskCompleterKind.Spawn));
                if (unseenCode)
                    Completed(handle, new TaskCompleter(caller.Id, spawn.OperationId, TaskCompleterKind.Unseen));
                foreach (var callee in callees.SelectMany(list => list))
                {
                    CheckCancellation();
                    if (spawn.AwaitsWorkTask)
                    {
                        var (values, unfollowed) = CompletionOf(callee);
                        Complete(handle, values, unfollowed,
                                 new TaskCompleter(callee.Id, -1, IsAsyncBody(callee.BodyId) ? TaskCompleterKind.Returns : TaskCompleterKind.ReturnedTasks));
                    }
                    else if (IsAsyncBody(callee.BodyId) && site.Tail is { } tail)
                    {
                        Complete(tail, callee.Returns, callee.ReturnsUnfollowed || unseen, new TaskCompleter(callee.Id, -1, TaskCompleterKind.Returns));
                        Complete(handle, [tail], false, new TaskCompleter(caller.Id, spawn.CallOperationId, TaskCompleterKind.Task));
                    }
                    else
                        Complete(handle, callee.Returns, callee.ReturnsUnfollowed, new TaskCompleter(callee.Id, -1, TaskCompleterKind.Returns));
                }
            }

            var state = spawn.State is { } stateValue ? Eval(caller, stateValue.Values) : [];
            var localValues = callees.Length == 3 && spawn.Kind is IrSpawnKind.ParallelFor or IrSpawnKind.ParallelForEach
                ? callees[0].Concat(callees[1]).SelectMany(callee => callee.Returns).ToTrackedSet(StringComparer.Ordinal)
                : null;
            for (var index = 0; index < callees.Length; index++)
            {
                CheckCancellation();
                var role = localValues is null ? SpawnRole.Work : index switch { 0 => SpawnRole.LocalInit, 1 => SpawnRole.Work, _ => SpawnRole.LocalFinally };
                foreach (var callee in callees[index])
                {
                    CheckCancellation();
                    switch (spawn.Kind)
                    {
                        case IrSpawnKind.ContinueWith:
                            BindParameter(callee, 0, Eval(caller, spawn.Antecedent!.Values));
                            BindParameter(callee, 1, state);
                            break;
                        case IrSpawnKind.StartNew or IrSpawnKind.QueueUserWorkItem or IrSpawnKind.UnsafeQueueUserWorkItem or IrSpawnKind.ThreadStart:
                            BindParameter(callee, 0, state);
                            break;
                    }

                    if (localValues is not null && role != SpawnRole.LocalInit)
                        BindParameter(callee, role == SpawnRole.Work ? ParameterCount(callee) - 1 : 0, localValues);
                    Add(callee.Requests, caller.Requests);
                    if (site.Callees.Add((callee.Id, role)))
                        _changes++;
                }
            }

            TrackedList<InstanceState> WorkCallees(string region)
            {
                if (_delegates.TryGetValue(region, out var work) &&
                    DelegateCallees(caller, spawn.OperationId, work, () => unseen = true) is { Count: > 0 } resolved)
                {
                    return resolved;
                }

                unseen = true;
                return [];
            }
        }

        private bool IsAsyncBody(string bodyId) =>
            _scope.Reachable.Bodies.TryGetValue(bodyId, out var body) && body is { IsAsync: true, IsAsyncIterator: false };

        /// <summary>The <c>Execute()</c> implementations a work item region runs, with the region as receiver.</summary>
        /// <param name="items">The items.</param>
        /// <param name="workMethod">The workMethod.</param>
        private TrackedList<InstanceState> WorkItemCallees(IEnumerable<string> items, string workMethod)
        {
            var callees = new TrackedList<InstanceState>();
            if (ReachableSet.WorkMethodId(_program, workMethod) is not { } methodId)
                return callees;
            foreach (var item in items)
            {
                CheckCancellation();
                callees.AddRange(DispatchCallees(item, methodId, []).Callees);
            }
            return callees;
        }

        /// <summary>Runs a timer's callback or <c>Elapsed</c> handler; a created timer's callback gets the timer's state.</summary>
        /// <param name="caller">The caller.</param>
        /// <param name="timer">The timer.</param>
        private void TimerCallback(InstanceState caller, SummaryTimer timer)
        {
            var site = SiteOf(_timerCallbacks, caller, timer.OperationId, () => new SiteState(null, timer.OperationId, timer.Action));
            Add(site.Handles, Eval(caller, timer.Timer.Values));
            var state = timer.State is { } stateValue ? Eval(caller, stateValue.Values) : [];
            foreach (var region in Eval(caller, timer.Callback!.Values).Where(_delegates.ContainsKey))
            {
                CheckCancellation();
                foreach (var callee in DelegateCallees(caller, timer.OperationId, _delegates[region], () => { }))
                {
                    CheckCancellation();
                    if (timer.Action == IrTimerAction.Create)
                        BindParameter(callee, 0, state);
                    Add(callee.Requests, caller.Requests);
                    if (site.Callees.Add((callee.Id, SpawnRole.Work)))
                        _changes++;
                }
            }
        }

        /// <summary>A <c>WhenAll</c> result is a region of its site whose group remembers the listed tasks, or that they are unknown, and
        /// which completes with an array of their completion values over <c>Task&lt;T&gt;</c>.</summary>
        /// <param name="caller">The caller.</param>
        /// <param name="whenAll">The whenAll.</param>
        private void WhenAll(InstanceState caller, SummaryWhenAll whenAll)
        {
            var group = TaskRegion(caller, whenAll.CallOperationId);
            Add(CallResult(caller, whenAll.CallOperationId), [group]);
            if (!_taskGroups.TryGetValue(group, out var members))
            {
                _taskGroups.Add(group, members = (new TrackedSet<string>(StringComparer.Ordinal), whenAll.TasksKnown));
                _changes++;
            }

            Add(members.Members, whenAll.Tasks.SelectMany(task => Eval(caller, task.Values)).ToArray());
            // Over Task<T> the group completes with a new array of the site, whose cells hold what every task completes with: the listed
            // ones, or those the argument holds. The array is an object the heap makes, so it is followed whatever its cells hold; over
            // Task the group completes with nothing.
            if (whenAll.ArrayTypeKey is not { } arrayType)
                return;
            var tasks = whenAll.TasksKnown
                ? whenAll.Tasks.SelectMany(task => Eval(caller, task.Values))
                : whenAll.Source is { } source ? Eval(caller, source.Values).SelectMany(collection => Load(collection, PathValue.ELEMENT)) : [];
            var array = CompletionArray(caller, whenAll.CallOperationId, arrayType);
            Add(Field(array, PathValue.ELEMENT), Completion(tasks.ToArray()));
            Complete(group, [array], false, new TaskCompleter(caller.Id, whenAll.OperationId, TaskCompleterKind.WhenAll));
        }

        /// <summary>What a BCL task member makes of a task's completion value (R1, R2): the new task of its site, which the call's result
        /// gives, or the existing tasks it names complete with a value, a value the heap does not follow, what other tasks complete with,
        /// or one of several tasks. A value crossing the completion slot keeps whether it may be an object the heap does not follow.</summary>
        /// <param name="instance">The instance making the call.</param>
        /// <param name="task">The task operation.</param>
        private void TaskOperation(InstanceState instance, SummaryTaskOperation task)
        {
            IReadOnlyCollection<string> targets;
            if (task.Task is { } existing)
                targets = Eval(instance, existing.Values).ToArray();
            else
            {
                var region = TaskRegion(instance, task.CallOperationId);
                Add(CallResult(instance, task.CallOperationId), [region]);
                targets = [region];
            }

            bool Unknown(SummaryValue value) => Unfollowed(instance, value.UnknownSources, value.SourceCalls, value.Completions);
            var (values, unfollowed) = task.Kind switch
            {
                IrTaskKind.Completed => (task.Values.SelectMany(value => Eval(instance, value.Values)).ToArray(), task.Values.Any(Unknown)),
                IrTaskKind.CompletionOf when Eval(instance, task.Values.SelectMany(value => value.Values)) is var tasks =>
                    (Completion(tasks).ToArray(), task.Values.Any(Unknown) || tasks.Any(_unfollowedCompletions.Contains)),
                // A task a collection holds is one its cells hold, and what was stored there is not followed to its origin.
                IrTaskKind.AnyOf when !task.ValuesKnown =>
                    (Eval(instance, task.Values.SelectMany(value => value.Values)).SelectMany(source => Load(source, PathValue.ELEMENT)).ToArray(), true),
                IrTaskKind.AnyOf => (task.Values.SelectMany(value => Eval(instance, value.Values)).ToArray(), task.Values.Any(Unknown)),
                _ => (Array.Empty<string>(), true)
            };
            foreach (var target in targets)
            {
                CheckCancellation();
                Complete(target, values, unfollowed, new TaskCompleter(instance.Id, task.OperationId, TaskCompleterKind.TaskOperation));
            }
        }

        /// <summary>Whether the regions of a timer callback site name every timer it may run. As in <c>TimerSteps</c>, a null or a field
        /// read before its first write is no timer, and a source call names one only when what it returned is among those regions: a call
        /// with no resolved implementation, or one returning a value the heap does not follow, may return any timer. A call whose task the
        /// timer is the completion of gives what that task completes with, followed as many times as the timer consumed it.</summary>
        /// <param name="callerId">The instance making the timer step.</param>
        /// <param name="operationId">The step's operation.</param>
        /// <param name="timers">The regions of the site.</param>
        private bool TimersKnown(string callerId, int operationId, IReadOnlySet<string> timers)
        {
            var caller = _instances[callerId];
            var timer = caller.Summary.Timers.First(step => step.OperationId == operationId).Timer;
            return !Unfollowed(caller, timer.UnknownSources, timer.SourceCalls, timer.Completions) &&
                   timer.SourceCalls.All(call => timer.Depths(call).All(depth =>
                       HeapSolution.Completions(CallResult(caller, call), depth, task => Completion([task])) is { Count: > 0 } result &&
                       result.All(timers.Contains)));
        }

        private SiteState SiteOf(TrackedMap<(string Caller, int Operation), SiteState> sites, InstanceState caller, int operationId,
                                 Func<SiteState> create)
        {
            if (!sites.TryGetValue((caller.Id, operationId), out var site))
            {
                sites.Add((caller.Id, operationId), site = create());
                _changes++;
            }

            return site;
        }

        /// <summary>Binds a parameter of a spawned or called-back body, when the body has it.</summary>
        /// <param name="callee">The instance of the spawned or called-back body.</param>
        /// <param name="ordinal">The parameter's ordinal.</param>
        /// <param name="regions">The regions the parameter is bound to.</param>
        private void BindParameter(InstanceState callee, int ordinal, IEnumerable<string> regions)
        {
            if (ordinal >= 0 && ordinal < ParameterCount(callee))
                Add(Parameter(callee, ordinal), regions);
        }

        private int ParameterCount(InstanceState instance) =>
            _scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) ? body.Parameters.Count : 0;

        /// <summary>The handle region an operation of an instance creates: a spawn's task, an async call's task, a
        /// <c>WhenAll</c> result or the new task of a BCL task member, one per site and context.</summary>
        /// <param name="instance">The instance making the operation.</param>
        /// <param name="operationId">The operation's call.</param>
        /// <param name="depth">For a known call's <c>task(…)</c> result, how many tasks deep this one stands inside the call's own: each
        /// level is a region of its own (R4).</param>
        private string TaskRegion(InstanceState instance, int operationId, int depth = 0)
        {
            var owner = _scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) ? body.OwnerSymbol : instance.BodyId;
            var level = depth == 0 ? "" : "@" + depth;
            return Region($"task|{instance.BodyId}#{operationId}{level}|{ContextKey(instance)}", HeapRegionKind.Task, $"task:{owner}#{operationId}{level}", null,
                          instance.Context, $"task|{instance.BodyId}#{operationId}{level}", merged: instance.IsMerged,
                          site: new CreationSite(instance.BodyId, operationId, "", 0));
        }

        /// <summary>The array a <c>WhenAll</c> over <c>Task&lt;T&gt;</c> completes with: an object of its site, one per site and context.</summary>
        /// <param name="instance">The instance making the call.</param>
        /// <param name="operationId">The call's operation.</param>
        /// <param name="arrayType">The array's type key.</param>
        private string CompletionArray(InstanceState instance, int operationId, string arrayType)
        {
            var typeKey = ProgramIndex.Substitute(arrayType, instance.Substitution);
            var owner = _scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) ? body.OwnerSymbol : instance.BodyId;
            return Region($"completion|{instance.BodyId}#{operationId}|{typeKey}|{ContextKey(instance)}", HeapRegionKind.Allocation,
                          $"alloc:{owner}#{DisplayType(typeKey)}", typeKey, instance.Context, $"completion|{instance.BodyId}#{operationId}",
                          merged: instance.IsMerged, site: new CreationSite(instance.BodyId, operationId, arrayType, 1), exactType: true);
        }

        private string TailRegion(string handle)
        {
            if (_tails.TryGetValue(handle, out var tail))
                return tail;
            var region = _regions[handle];
            tail = Region($"{handle}|tail", HeapRegionKind.Task, $"{region.Display}#tail", null, region.Context, $"{region.Group}|tail",
                          merged: region.IsMerged, site: new CreationSite(region.SiteBodyId!, region.SiteOperationId!.Value, "", 0));
            _tails.Add(handle, tail);
            return tail;
        }

        /// <summary>What tasks complete with: what their completion slots hold.</summary>
        /// <param name="tasks">The task regions.</param>
        private TrackedSet<string> Completion(IEnumerable<string> tasks)
        {
            var result = new TrackedSet<string>(StringComparer.Ordinal);
            foreach (var task in tasks)
            {
                CheckCancellation();
                result.UnionWith(Load(task, COMPLETION_FIELD));
            }
            return result;
        }

        /// <summary>The objects fresh values name that the solve made and completed as tasks: a <c>TaskCompletionSource</c> stands for its
        /// own task. Each is looked up as a query, so a value the solve never evaluated names no region and adds none.</summary>
        /// <param name="instance">The instance expressing the values.</param>
        /// <param name="values">The fresh values.</param>
        private TrackedSet<string> SolvedTasks(InstanceState instance, AllocationValue[] values) =>
            values.Length == 0
                ? new TrackedSet<string>(StringComparer.Ordinal)
                : Query((AllocationValue[] fresh) => Eval(instance, fresh.Cast<AbstractValue>())
                                                     .Where(region => _regions.ContainsKey(region) && _fields.ContainsKey((region, COMPLETION_FIELD)))
                                                     .ToTrackedSet(StringComparer.Ordinal))(values);

        /// <summary>The regions, and what the tasks among them complete with, at any depth: what a value carries wherever it goes, as each
        /// of those values handed over directly would be carried (R2). Every escape decider reads it.</summary>
        /// <param name="regions">The regions to start from.</param>
        private TrackedSet<string> Carried(IEnumerable<string> regions)
        {
            var carried = new TrackedSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(regions);
            while (pending.TryPop(out var region))
            {
                CheckCancellation();
                if (!carried.Add(region) || !_fields.TryGetValue((region, COMPLETION_FIELD), out var completion))
                    continue;
                foreach (var value in completion)
                    pending.Push(value);
            }
            return carried;
        }

        /// <summary>Completes a task with values, and marks its completion as one that may be an object the heap does not follow when what
        /// completes it is, where it is produced, or the task's producer is code the analysis does not see in full. An empty, followed
        /// completion is a known default.</summary>
        /// <param name="task">The task region.</param>
        /// <param name="values">The regions it completes with.</param>
        /// <param name="unfollowed">Whether what completes it may be such an object.</param>
        /// <param name="completer">Where what completes it comes from, for the export (<see cref="Completed"/>).</param>
        private void Complete(string task, IEnumerable<string> values, bool unfollowed, TaskCompleter completer)
        {
            var completed = values.ToArray();
            if (completed.Length != 0)
                Add(Field(task, COMPLETION_FIELD), completed);
            if (unfollowed && _unfollowedCompletions.Add(task))
                _changes++;
            Completed(task, completer);
        }

        /// <summary>Records where what completes a task region comes from (<see cref="HeapSolution.TaskCompleters"/>); no rule of the
        /// solve reads it, so it changes nothing the solve depends on.</summary>
        /// <param name="task">The task region.</param>
        /// <param name="completer">Where what completes it comes from.</param>
        private void Completed(string task, TaskCompleter completer) => _completers.Add((task, completer));

        /// <summary>What one run of a body completes with, and whether it may be an object the heap does not follow: an async body's returns,
        /// or what the tasks a non-async body returns complete with.</summary>
        /// <param name="callee">The body's instance.</param>
        private (TrackedSet<string> Values, bool Unfollowed) CompletionOf(InstanceState callee) =>
            IsAsyncBody(callee.BodyId)
                ? (callee.Returns, callee.ReturnsUnfollowed)
                : (Completion(callee.Returns), callee.ReturnsUnfollowed || callee.Returns.Any(_unfollowedCompletions.Contains));

        /// <summary>Models a service call: a created scope is an allocation of the calling execution whose <c>ServiceProvider</c>
        /// stands for it; a locator call with a constant type resolves in each scope its receiver stands for, and
        /// <c>GetServices</c> gives a collection of every supported registration's object.</summary>
        /// <param name="instance">The instance.</param>
        /// <param name="call">The call.</param>
        private void Locate(InstanceState instance, SummaryOpaqueCall call)
        {
            if (call.ServiceCall is not { } service)
            {
                if (call.Callee.Contains("ServiceProvider", StringComparison.Ordinal))
                {
                    var scopes = Eval(instance, call.Receivers).Where(_scopeObjects.Contains).ToArray();
                    Add(CallResult(instance, call.OperationId), scopes.Select(scope => Provider(new ResolutionScope(null, scope))));
                }

                return;
            }

            var owner = _scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) ? body.OwnerSymbol : instance.BodyId;
            var site = new CreationSite(instance.BodyId, call.OperationId, "", 0);
            if (service.Kind == IrServiceCallKind.ScopeCreation)
            {
                var created = Region($"scope|{instance.BodyId}#{call.OperationId}|{ContextKey(instance)}", HeapRegionKind.Allocation,
                                     $"scope:{owner}#{call.OperationId}", null, instance.Context, $"scope|{instance.BodyId}#{call.OperationId}",
                                     merged: instance.IsMerged, site: site);
                _scopeObjects.Add(created);
                Add(CallResult(instance, call.OperationId), [created]);
                return;
            }

            if (service.ServiceTypeKey is not { } key)
                return;
            var resolution = _scope.DiIndex.Resolve(key);
            foreach (var scope in Scopes(instance, call))
            {
                CheckCancellation();
                var resolver = $"locator|{instance.BodyId}#{call.OperationId}";
                IReadOnlyList<string> regions = service.Kind == IrServiceCallKind.Locator
                    ? Resolve(resolution, scope, resolver, scope.Context, "")
                    : Supported(resolution).Select(registration => Registration(key, registration, scope, resolver, scope.Context, "")).ToArray();
                foreach (var region in regions.Where(region => _regions[region] is { Kind: HeapRegionKind.Di, Context: not ("singleton" or "root-scope") } di &&
                                                               !di.Context.StartsWith("invocation:", StringComparison.Ordinal)))
                {
                    CheckCancellation();
                    ConstructionEdges(instance, call.OperationId, region);
                    if (!_locatorCreators.TryGetValue(region, out var creators))
                        _locatorCreators.Add(region, creators = new TrackedSet<string>(StringComparer.Ordinal));
                    Add(creators, [instance.Id]);
                }

                Trigger(instance, call.OperationId, regions);
                if (service.Kind == IrServiceCallKind.Locator)
                {
                    Inject(CallResult(instance, call.OperationId), regions);
                    continue;
                }

                var collection = Region($"services|{instance.BodyId}#{call.OperationId}|{ContextKey(instance)}|{scope.Context}", HeapRegionKind.Allocation,
                                        $"services:{owner}#{DisplayType(key)}", null, instance.Context, $"services|{instance.BodyId}#{call.OperationId}",
                                        merged: instance.IsMerged, site: site);
                if (!_collections.TryGetValue(collection, out var elements))
                    _collections.Add(collection, elements = []);
                foreach (var region in regions.Where(region => !elements.Contains(region)))
                {
                    CheckCancellation();
                    elements.Add(region);
                }
                Inject(Field(collection, PathValue.ELEMENT), regions);
                Add(CallResult(instance, call.OperationId), [collection]);
            }
        }

        /// <summary>The scopes a locator call's receiver stands for: the instance's requests for <c>RequestServices</c>, the root scope
        /// for the host's and application's services, and the scope of each provider region an injected or scope provider, or a root
        /// entry's own provider parameter, points to. Any other provider stands for no scope.</summary>
        /// <param name="instance">The instance.</param>
        /// <param name="call">The call.</param>
        private IReadOnlyList<ResolutionScope> Scopes(InstanceState instance, SummaryOpaqueCall call)
        {
            // An extension method takes the provider as its first argument.
            var provider = call.Receivers.Count != 0
                ? call.Receivers
                : call.Arguments.OrderBy(argument => argument.ParameterOrdinal).FirstOrDefault()?.Values ?? new TrackedSet<AbstractValue>();
            switch (call.ServiceCall!.Provider)
            {
                case IrProviderKind.RequestServices:
                    return instance.Requests.Order(StringComparer.Ordinal).Select(request => new ResolutionScope(request)).ToArray();
                case IrProviderKind.HostServices or IrProviderKind.ApplicationServices:
                    return [new ResolutionScope(null)];
                case null when !_rootInstances.ContainsValue(instance.Id) && !_factoryInstances.ContainsKey(instance.Id) ||
                               provider.Count == 0 || !provider.All(value => value is ParameterValue):
                    return [];
                default:
                    return Eval(instance, provider).Order(StringComparer.Ordinal)
                                                         .Select(region => _providers.GetValueOrDefault(region))
                                                         .OfType<ResolutionScope>()
                                                         .ToArray();
            }
        }

        private void Trigger(InstanceState instance, int operationId, IEnumerable<string> regions)
        {
            foreach (var region in regions)
            {
                CheckCancellation();
                if (_regionTriggers.Add((instance.Id, operationId, region)))
                    _changes++;
            }
        }

        private void ConstructionEdges(InstanceState caller, int operationId, string region)
        {
            foreach (var constructor in _constructions.GetValueOrDefault(region)?.ToArray() ?? [])
            {
                CheckCancellation();
                AddEdge((caller.Id, operationId, constructor, CONSTRUCTION_REASON));
            }
        }

        private void Call(InstanceState caller, CallTransfer call)
        {
            var typeArguments = (call.TargetMethodTypeArgumentKeys ?? []).Select(key => ProgramIndex.Substitute(key, caller.Substitution)).ToArray();
            var containing = call.TargetContainingTypeKey is null ? null : ProgramIndex.Substitute(call.TargetContainingTypeKey, caller.Substitution);
            TrackedSet<string>? evaluatedReceivers = null;
            TrackedSet<string> Receivers() => evaluatedReceivers ??= Eval(caller, call.Receivers);
            switch (call.Kind)
            {
                case IrCallKind.LocalFunction:
                {
                    var callee = Instance(call.Target, OwnerContext(caller.CellOwners), caller.Substitution, caller.Receivers, caller.CellOwners,
                                          caller.IsReceiverless);
                    Bind(caller, call, callee, "exact");
                    return;
                }
                case IrCallKind.Delegate:
                {
                    var regions = Eval(caller, call.Receivers).Where(region => _delegates.ContainsKey(region)).ToArray();
                    if (regions.Length == 0)
                    {
                        NoReceiver(caller, call);
                        _rebuilding.UnresolvedDispatches.Add((caller.Id, call.OperationId));
                        Handoff(caller, call.OperationId, call.Arguments.SelectMany(argument => argument.Values));
                    }
                    UnresolvedTargets(caller, call);
                    foreach (var region in regions)
                    {
                        CheckCancellation();
                        CallDelegate(caller, call, _delegates[region]);
                    }
                    return;
                }
                case IrCallKind.Virtual or IrCallKind.Interface:
                {
                    if (DeadReceiver(caller, call, containing, Receivers))
                        return;
                    var receivers = EligibleReceivers(Receivers(), containing);
                    var iteratorReceivers = IteratorMember(caller, call.OperationId, receivers);
                    receivers.ExceptWith(iteratorReceivers);
                    if (receivers.Count == 0)
                    {
                        if (iteratorReceivers.Count == 0)
                        {
                            NoReceiver(caller, call);
                            _rebuilding.UnresolvedDispatches.Add((caller.Id, call.OperationId));
                            Handoff(caller, call.OperationId, call.Arguments.SelectMany(argument => argument.Values));
                        }
                    }
                    UnresolvedTargets(caller, call);
                    foreach (var receiver in receivers)
                    {
                        CheckCancellation();
                        Dispatch(caller, call, call.Target, receiver, typeArguments, containing,
                                 _regions[receiver].Kind == HeapRegionKind.Di ? "di-binding" : "points-to");
                    }
                    return;
                }
                case IrCallKind.Static:
                {
                    if (_program.Method(call.Target) is not { } method)
                        return;
                    var callee = Instance(call.Target, $"{caller.BodyId}#{call.OperationId}", Substitution(method, containing, typeArguments), [], [], false);
                    Bind(caller, call, callee, "exact");
                    ActivateTypeInitializer(containing ?? method.ContainingTypeKey, caller, null);
                    return;
                }
                default:
                {
                    if (_program.Method(call.Target) is not { } method)
                        return;
                    if (DeadReceiver(caller, call, containing, Receivers))
                        return;
                    var receivers = EligibleReceivers(Receivers(), containing);
                    var iteratorReceivers = IteratorMember(caller, call.OperationId, receivers);
                    receivers.ExceptWith(iteratorReceivers);
                    if (receivers.Count == 0)
                    {
                        if (iteratorReceivers.Count == 0)
                        {
                            NoReceiver(caller, call);
                            _receiverlessCalls.TryAdd((caller.Id, call.OperationId), (caller, call));
                        }
                    }

                    foreach (var receiver in receivers)
                    {
                        CheckCancellation();
                        var callee = Instance(call.Target, receiver + TypeArgumentText(typeArguments),
                                              ReceiverSubstitution(_regions[receiver], method, typeArguments), [receiver], [], false);
                        Bind(caller, call, callee, "exact");
                    }

                    if (call.Kind == IrCallKind.Constructor)
                        ActivateTypeInitializer(containing ?? method.ContainingTypeKey, caller, null);
                    return;
                }
            }
        }

        private TrackedSet<string> IteratorMember(InstanceState caller, int operationId, IEnumerable<string> receivers)
        {
            var handled = new TrackedSet<string>(StringComparer.Ordinal);
            if (!_scope.Reachable.Bodies.TryGetValue(caller.BodyId, out var body) ||
                body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
                    .FirstOrDefault(call => call.Id == operationId) is not { } operation)
                return handled;

            var member = IteratorMemberOf(operation.Method);
            if (member == IrEnumerationRole.None)
                return handled;
            foreach (var region in receivers)
            {
                CheckCancellation();
                if (!_iteratorObjects.ContainsKey(region) && !_sequences.ContainsKey(region) && !_groupings.Contains(region))
                    continue;
                handled.Add(region);
                if (operation.EnumerationRole is IrEnumerationRole.MoveNext or IrEnumerationRole.Current or IrEnumerationRole.Dispose &&
                    ForeachAlreadyEnumerated(caller, operation, region))
                    continue;
                switch (member)
                {
                    case IrEnumerationRole.GetEnumerator:
                        Add(CallResult(caller, operationId), [region]);
                        break;
                    case IrEnumerationRole.MoveNext or IrEnumerationRole.Dispose:
                        Consume(caller, operationId, [region], new TrackedSet<string>(StringComparer.Ordinal));
                        break;
                    case IrEnumerationRole.Current:
                        Add(CallResult(caller, operationId), _sequences.TryGetValue(region, out var sequence)
                            ? sequence.Yields : Load(region, PathValue.ELEMENT));
                        break;
                }
            }

            if (handled.Count != 0)
            {
                if (!_iteratorMemberReceivers.TryGetValue((caller.Id, operationId), out var recorded))
                    _iteratorMemberReceivers.Add((caller.Id, operationId), recorded = new TrackedSet<string>(StringComparer.Ordinal));
                recorded.UnionWith(handled);
            }
            return handled;
        }

        private static IrEnumerationRole IteratorMemberOf(string method) => method switch
        {
            var name when name.EndsWith(".GetEnumerator()", StringComparison.Ordinal) => IrEnumerationRole.GetEnumerator,
            var name when name.EndsWith(".MoveNext()", StringComparison.Ordinal) => IrEnumerationRole.MoveNext,
            var name when name.EndsWith(".get_Current()", StringComparison.Ordinal) => IrEnumerationRole.Current,
            var name when name.EndsWith(".Dispose()", StringComparison.Ordinal) => IrEnumerationRole.Dispose,
            _ => IrEnumerationRole.None
        };

        private bool ForeachAlreadyEnumerated(InstanceState caller, IrCallOperation operation, string region)
        {
            if (operation.EnumerationId is not int enumeration)
                return false;
            var body = _scope.Reachable.Bodies[caller.BodyId];
            var get = body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
                          .FirstOrDefault(call => call.EnumerationId == enumeration && call.EnumerationRole == IrEnumerationRole.GetEnumerator);
            if (get is null)
                return false;
            var receivers = caller.Summary.OpaqueCalls.FirstOrDefault(call => call.OperationId == get.Id)?.Receivers ??
                            caller.Summary.Calls.FirstOrDefault(call => call.OperationId == get.Id)?.Receivers;
            return receivers is not null && Eval(caller, receivers).Contains(region);
        }

        private TrackedSet<string> EligibleReceivers(TrackedSet<string> receivers, string? containing)
        {
            if (containing is not null)
                receivers.RemoveWhere(region => _typeSafety.CannotBe(_regions[region], containing));
            return receivers;
        }

        private bool DeadReceiver(InstanceState caller, CallTransfer call, string? containing,
                                  Func<TrackedSet<string>>? evaluateReceivers = null)
        {
            if (containing is null)
                return false;
            var receivers = evaluateReceivers is null ? Eval(caller, call.Receivers) : evaluateReceivers();
            return receivers.Count != 0 && receivers.All(region => _typeSafety.CannotBe(_regions[region], containing)) &&
                   !call.ReceiverUnknownSources.Contains(UnknownSource.OpaqueCall) &&
                   !call.ReceiverUnknownSources.Contains(UnknownSource.Other) &&
                   !call.ReceiverSourceCalls.Any(caller.UnfollowedCallResults.Contains) &&
                   !call.ReceiverCompletions.Any(caller.UnfollowedCompletions.Contains);
        }

        /// <summary>The delegates among values an unresolved call is handed, each run by the instances of its target in an unknown
        /// execution of its own (R3, ADR 0011). A delegate handed to two such calls is one execution, which both sites started.</summary>
        /// <param name="caller">The caller.</param>
        /// <param name="operationId">The operationId.</param>
        /// <param name="values">The values.</param>
        private void Handoff(InstanceState caller, int operationId, IEnumerable<AbstractValue> values)
        {
            // An array handed over hands over what it holds: a params array, one created in the argument's place, or any other (R1). A
            // collection hands over what its storages hold, as an array does (ADR 0010, phase 5b second run).
            var handed = Eval(caller, values);
            handed.UnionWith(handed.Where(region => InterproceduralAccesses.IsCollectionType(_program, _regions[region].TypeKey))
                                   .SelectMany(collection => Load(collection, PathValue.ELEMENT).Concat(Load(collection, PathValue.KEYS)))
                                   .ToArray());
            foreach (var region in handed.Where(_delegates.ContainsKey))
            {
                CheckCancellation();
                if (!_rebuilding.Handoffs.TryGetValue(region, out var handoff))
                    _rebuilding.Handoffs.Add(region, handoff = ([], new TrackedSet<string>(StringComparer.Ordinal)));
                handoff.Sites.Add((caller.Id, operationId));
                foreach (var callee in DelegateCallees(caller, operationId, _delegates[region], () => { }))
                {
                    CheckCancellation();
                    Add(callee.Requests, caller.Requests);
                    handoff.Callees.Add(callee.Id);
                }
            }
        }

        /// <summary>A delegate invocation runs the delegate's target: its nested body with the creating instance's cells and captured
        /// receiver, a static method at the invocation site, or an instance method on each captured receiver region, resolved
        /// through the program index when the method is virtual.</summary>
        /// <param name="caller">The caller.</param>
        /// <param name="call">The call.</param>
        /// <param name="state">The state.</param>
        private TrackedList<InstanceState> CallDelegate(InstanceState caller, CallTransfer call, DelegateState state)
        {
            var callees = DelegateCallees(caller, call.OperationId, state, () => NoReceiver(caller, call));
            foreach (var callee in callees)
            {
                CheckCancellation();
                Bind(caller, call, callee, "delegate");
            }
            // A method group whose target runs no body the analysis has is as unresolved as a call of that target (R1).
            if (callees.Count == 0 && !state.IsNestedBody)
            {
                var declaring = _program.Method(state.Target)?.ContainingTypeKey;
                Unresolved(caller, call, state.CapturedReceivers.Select(receiver => (receiver, declaring)).ToArray());
            }

            return callees;
        }

        /// <summary>
        /// What a copy from a collection holds, decided for each object the source may be, whatever type the argument was declared
        /// as (ADR 0010, phase 5b second run): a dictionary yields its keys and its values apart, which a dictionary copy keeps apart
        /// and any other copy holds as pairs; anything else yields what its cells hold, and a dictionary built from pairs takes each
        /// pair's key and value.
        /// </summary>
        /// <param name="instance">The instance.</param>
        /// <param name="copy">The copy.</param>
        private void Copy(InstanceState instance, ElementTransfer copy)
        {
            var targets = Eval(instance, copy.Arrays);
            foreach (var source in Eval(instance, copy.CopiedFrom!))
            {
                CheckCancellation();
                var isDictionary = InterproceduralAccesses.IsDictionaryType(_program, _regions[source].TypeKey);
                TrackedSet<string> held;
                if (copy.Pair is null)
                {
                    held = isDictionary
                        ? Load(source, copy.Slot)
                        : Load(source, PathValue.ELEMENT).SelectMany(pair => Load(pair, copy.Slot)).ToTrackedSet(StringComparer.Ordinal);
                }
                else if (isDictionary)
                {
                    held = Eval(instance, copy.Pair);
                    foreach (var pair in held)
                    {
                        CheckCancellation();
                        Add(Field(pair, PathValue.KEYS), Load(source, PathValue.KEYS));
                        Add(Field(pair, PathValue.ELEMENT), Load(source, PathValue.ELEMENT));
                    }
                }
                else
                    held = Load(source, PathValue.ELEMENT);

                foreach (var target in targets)
                {
                    CheckCancellation();
                    Add(Field(target, copy.Slot), held);
                }
            }
        }

        /// <summary>
        /// The factories of <c>GetOrAdd</c> and <c>AddOrUpdate</c> run where the call stands, as a call of the delegate there would
        /// (R11): each is handed the key argument, the value the dictionary holds, or the overload's own argument, as its parameters
        /// ask; what it returns is held by the dictionary and is what the call returns. The delegate itself is held by nothing. So since
        /// phase 5c do the delegates of the other members of the table that take one, handed what the cells hold (ADR 0010).
        /// </summary>
        /// <param name="caller">The caller.</param>
        /// <param name="call">The call.</param>
        private void RunFactories(InstanceState caller, SummaryOpaqueCall call)
        {
            var member = call.Collection!;
            IReadOnlySet<AbstractValue> Argument(int? ordinal) =>
                call.Arguments.FirstOrDefault(argument => argument.ParameterOrdinal == ordinal)?.Values ?? new TrackedSet<AbstractValue>();
            var key = Argument(member.KeyArgument);
            var handed = Argument(member.FactoryArgument);
            var held = call.Receivers.Select(receiver => receiver is PathValue { IsWildcard: true } ? receiver
                                                          : receiver is PathValue path ? new PathValue(path.Base, [.. path.Segments, PathValue.ELEMENT])
                                                          : new PathValue(receiver, [PathValue.ELEMENT]))
                                     .ToTrackedSet();
            // What a factory makes, the dictionary holds; what a list's converter returns, the list `ConvertAll` creates does, and a
            // list `FindAll` creates holds the cells of the one it was called on (ADR 0010, phase 5c). The other delegates of the
            // table, a predicate, an action and a comparison, return no object.
            var created = member.ResultTypeKey is { } resultType ? CreatedAtCall(caller, call, resultType) : null;
            if (created is not null && member.Member.EndsWith(".FindAll", StringComparison.Ordinal))
                Add(Field(created, PathValue.ELEMENT), Eval(caller, held));
            var holders = created is not null && member.Member.EndsWith(".ConvertAll", StringComparison.Ordinal) ? [created] : Eval(caller, call.Receivers);
            for (var index = 0; index < member.Factories.Count && index < member.FactoryInputs.Count; index++)
            {
                CheckCancellation();
                var inputs = member.FactoryInputs[index];
                var factory = Argument(member.Factories[index]);
                var invocation = new CallTransfer(call.OperationId, call.Callee, IrCallKind.Delegate, factory,
                                                  inputs.Select((input, ordinal) => new CallArgument(ordinal, input switch
                                                        {
                                                            IrFactoryInput.Key => key,
                                                            IrFactoryInput.Held => held,
                                                            _ => handed
                                                        }))
                                                        .ToArray(),
                                                  []);
                // As a call of the delegate would: a factory with no body at hand is an unresolved dispatch there, and so is one no
                // delegate object is known for, such as one an opaque call handed back (R1).
                var regions = Eval(caller, factory).Where(_delegates.ContainsKey).ToArray();
                if (regions.Length == 0)
                {
                    NoReceiver(caller, invocation);
                    Unresolved(caller, invocation, []);
                    Seen(inputs);
                }

                foreach (var region in regions)
                {
                    CheckCancellation();
                    var state = _delegates[region];
                    var callees = CallDelegate(caller, invocation, state);
                    foreach (var callee in callees)
                    {
                        CheckCancellation();
                        foreach (var holder in holders)
                        {
                            CheckCancellation();
                            Add(Field(holder, PathValue.ELEMENT), callee.Returns);
                        }
                    }

                    if (callees.Count == 0 && !state.IsNestedBody)
                        Seen(inputs);
                }
            }

            // The unresolved dispatch sees what the factories left unresolved are handed and nothing another factory of the call is.
            void Seen(IEnumerable<IrFactoryInput> inputs)
            {
                if (!_rebuilding.UnresolvedFactoryInputs.TryGetValue((caller.Id, call.OperationId), out var seen))
                    _rebuilding.UnresolvedFactoryInputs.Add((caller.Id, call.OperationId), seen = []);
                seen.UnionWith(inputs);
            }
        }

        /// <summary>
        /// The delegates a known call receives run as their fates say (ADR 0012, R3), each handed what its inputs name, evaluated at the
        /// call: an <c>invoke-now</c> one at the call, in the caller's execution, as a call of it there would, and nothing keeps it; a
        /// <c>startup</c> one in startup for a call startup makes and in an unknown execution for any other; an <c>unknown-execution</c> one
        /// in an unknown execution, as one an opaque call is handed, though the call is known. What the call returns is what its result
        /// says, and nothing where it says nothing. A delegate no delegate object is known for, or a method group whose target has no body
        /// the run has, is an unresolved dispatch at the call, as a call of it there would be.
        /// </summary>
        /// <param name="caller">The instance making the known call.</param>
        /// <param name="call">The call carrying the model.</param>
        private void RunFates(InstanceState caller, SummaryOpaqueCall call)
        {
            var library = call.Library!;
            if (!_keeperCalls.TryGetValue((caller.Id, call.OperationId), out var projections))
                _keeperCalls.Add((caller.Id, call.OperationId), projections = []);
            if (projections.TryGetValue(library, out var previous))
            {
                var receivers = previous.Receivers.Concat(call.Receivers).ToTrackedSet();
                var arguments = previous.Arguments.Concat(call.Arguments).GroupBy(argument => argument.ParameterOrdinal)
                    .Select(group => new CallArgument(group.Key, group.SelectMany(argument => argument.Values).ToTrackedSet())
                    {
                        References = group.SelectMany(argument => argument.References).Distinct().ToArray(),
                        UnknownSources = group.SelectMany(argument => argument.UnknownSources).ToHashSet(),
                        SourceCalls = group.SelectMany(argument => argument.SourceCalls).ToHashSet(),
                        Completions = group.SelectMany(argument => argument.Completions).ToHashSet()
                    }).ToArray();
                // Reuse unchanged immutable inputs so the map's setter sees a no-op as a no-op.
                projections[library] = call with
                {
                    Receivers = previous.Receivers.SequenceEqual(receivers) ? previous.Receivers : receivers,
                    Arguments = previous.Arguments.Count == arguments.Length && previous.Arguments.Zip(arguments).All(pair =>
                        pair.First.ParameterOrdinal == pair.Second.ParameterOrdinal && pair.First.Values.SequenceEqual(pair.Second.Values) &&
                        pair.First.References.SequenceEqual(pair.Second.References)) ? previous.Arguments : arguments
                };
            }
            else
                projections[library] = call;
            var runs = new TrackedMap<int, TrackedList<(string Region, InstanceState Callee)>>();
            // The delegates some alternative of which runs code the analysis does not see in full: no delegate object is known for an
            // alternative, one resolves no body, or a dispatch of one has no receiver.
            var unseen = new HashSet<int>();
            // A not-run delegate is neither run nor kept by the call (ADR 0015): it has no callees, no missing receiver and no
            // unresolved fate, though its parameter still counts as fated.
            foreach (var fate in library.Fates.Where(fate => fate.Kind != IrFateKind.NotRun))
            {
                CheckCancellation();
                var invocation = Invocation(call, fate, []);
                var delegates = Eval(caller, invocation.Receivers).Where(_delegates.ContainsKey).ToArray();
                runs[fate.ParameterOrdinal] = delegates.SelectMany(region => DelegateCallees(caller, call.OperationId, _delegates[region], () =>
                                                           {
                                                               NoReceiver(caller, invocation);
                                                               unseen.Add(fate.ParameterOrdinal);
                                                           })
                                                           .Select(callee => (region, callee)))
                                                       .ToTrackedList();
                var argument = call.Arguments.FirstOrDefault(candidate => candidate.ParameterOrdinal == fate.ParameterOrdinal);
                if (delegates.Length == 0 || delegates.Any(region => runs[fate.ParameterOrdinal].All(run => run.Region != region)) ||
                    argument is not null && Unfollowed(caller, argument.UnknownSources, argument.SourceCalls, argument.Completions))
                {
                    unseen.Add(fate.ParameterOrdinal);
                }
            }

            // What the delegates return so far, for the inputs and the result naming it: the analysis does not order a delegate's runs,
            // and the fixpoint runs the call again as they grow. An iterator delegate's runs are there to be had where the entry step lets
            // a model name them.
            var returns = ReturnsOf(caller, call, runs, unseen);
            // The library sequence the call returns keeps its iterator delegates; nothing of it runs here (R5).
            var sequence = library.Result?.Leaf.Kind == IrResultKind.Sequence ? NewSequence(caller, call, "result", nested: false) : null;
            // What a delegate returned that the model names the elements of is enumerated as an argument would be: at the call when the
            // call returns no library sequence, else as a source of the one it returns, by whoever enumerates it (R3, R5).
            Consume(caller, call.OperationId, EnumeratedReturned(caller, call, library.EnumeratedReturns(false), library.EnumeratedCompletions(false), returns),
                    new TrackedSet<string>(StringComparer.Ordinal));
            if (sequence is not null)
                AddReturnedSources(sequence, EnumeratedReturned(caller, call, library.EnumeratedReturns(true), library.EnumeratedCompletions(true), returns));
            foreach (var fate in library.Fates.Where(fate => fate.Kind != IrFateKind.NotRun))
            {
                CheckCancellation();
                if (fate.Kind == IrFateKind.Holder)
                {
                    Hold(caller, call, fate, returns);
                    continue;
                }

                var inputs = fate.Inputs.Select((input, ordinal) => new CallArgument(ordinal, input.SelectMany((value, position) =>
                                                                                                        ModelValues(caller, call, value, returns,
                                                                                                                    $"fate{fate.ParameterOrdinal}.{ordinal}.{position}"))
                                                                                                    .Select(region => (AbstractValue)new RegionValue(region))
                                                                                                    .ToTrackedSet()))
                                 .ToArray();
                var invocation = Invocation(call, fate, inputs);
                var regions = Eval(caller, invocation.Receivers).Where(_delegates.ContainsKey).ToArray();
                if (regions.Length == 0)
                {
                    if (sequence is not null && fate.Kind == IrFateKind.Iterator)
                    {
                        sequence.Unresolved[(fate.ParameterOrdinal, UNRESOLVED_DELEGATE)] = (call.Callee, inputs, []);
                        continue;
                    }

                    NoReceiver(caller, invocation);
                    UnresolvedFate(caller, invocation, []);
                    continue;
                }

                foreach (var region in regions)
                {
                    CheckCancellation();
                    var state = _delegates[region];
                    var callees = runs[fate.ParameterOrdinal].Where(run => run.Region == region).Select(run => run.Callee).ToArray();
                    if (callees.Length == 0 && !state.IsNestedBody)
                    {
                        var declaring = _program.Method(state.Target)?.ContainingTypeKey;
                        var receivers = state.CapturedReceivers.Select(receiver => (receiver, declaring)).ToArray();
                        if (sequence is not null && fate.Kind == IrFateKind.Iterator)
                            sequence.Unresolved[(fate.ParameterOrdinal, region)] = (call.Callee, inputs, receivers);
                        else
                            UnresolvedFate(caller, invocation, receivers);
                    }

                    foreach (var callee in callees)
                    {
                        CheckCancellation();
                        foreach (var input in inputs)
                        {
                            CheckCancellation();
                            Add(Parameter(callee, input.ParameterOrdinal), Eval(caller, input.Values));
                        }
                        _rebuilding.FateRuns.Add((new DelegateSite(caller.Id, call.OperationId, fate.ParameterOrdinal), callee.Id));
                        Add(callee.Requests, caller.Requests);
                        switch (fate.Kind)
                        {
                            case IrFateKind.InvokeNow:
                                RunNow(caller, invocation, callee);
                                break;
                            case IrFateKind.Iterator when sequence is not null:
                                if (sequence.Callees.Add(callee.Id))
                                    _changes++;
                                break;
                            case IrFateKind.Startup:
                                _rebuilding.StartupDelegates.Add((caller.Id, call.OperationId, region, callee.Id));
                                break;
                            case IrFateKind.UnknownExecution:
                                if (!_rebuilding.Handoffs.TryGetValue(region, out var handoff))
                                    _rebuilding.Handoffs.Add(region, handoff = ([], new TrackedSet<string>(StringComparer.Ordinal)));
                                handoff.Sites.Add((caller.Id, call.OperationId));
                                handoff.Callees.Add(callee.Id);
                                break;
                        }
                    }
                }
            }

            if (library.Result is { } result)
                ModelResult(caller, call, result, returns, sequence, new ResultDestination(null, library.ResultTypeKey));
            foreach (var output in library.Outputs.OrderBy(output => output.Key))
            {
                CheckCancellation();
                var destination = new ResultDestination(output.Key, library.OutputTypeKeys[output.Key]);
                var outputSequence = output.Value.Kind == IrResultKind.Sequence ? NewSequence(caller, call, destination.Path, nested: true, destination) : null;
                ModelResult(caller, call, output.Value, returns, outputSequence, destination);
                foreach (var argument in call.Arguments.Where(argument => argument.ParameterOrdinal == output.Key))
                {
                    CheckCancellation();
                    foreach (var target in argument.References)
                    {
                        CheckCancellation();
                        foreach (var location in ReferenceLocations(caller, target))
                        {
                            CheckCancellation();
                            Add(Field(location.Region, location.Slot), RefResult(caller, call.OperationId, output.Key));
                        }
                    }
                }
            }

            foreach (var keep in library.Keeps)
            {
                CheckCancellation();
                var key = (caller.Id, call.OperationId, keep.Key);
                if (!_libraryKeeping.TryGetValue(key, out var kept))
                    _libraryKeeping.Add(key, kept = new TrackedSet<string>(StringComparer.Ordinal));
                Add(kept, keep.Value.SelectMany((value, position) => ModelValues(caller, call, value, returns, $"keep{keep.Key}.{position}")));
                var unfollowedKept = caller.UnfollowedModelCompletions.Contains((call.OperationId, $"keep{keep.Key}"));
                var unfollowedValue = keep.Value.Any(value => ModelUnfollowed(caller, call, value, returns));
                foreach (var keeper in KeeperTargets(caller, call, keep.Key, read: false))
                {
                    CheckCancellation();
                    Add(Field(keeper, PathValue.KEPT), kept);
                    if (unfollowedKept && _unfollowedKept.Add(keeper))
                        _changes++;
                    if (unfollowedValue && _unfollowedKeptValues.Add(keeper))
                        _changes++;
                }
            }
            foreach (var store in library.Stores)
            {
                CheckCancellation();
                var values = store.Value.SelectMany((value, position) => ModelValues(caller, call, value, returns, $"store{store.Key}.{position}")).ToArray();
                var key = (caller.Id, call.OperationId, store.Key);
                if (!_libraryStored.TryGetValue(key, out var stored))
                    _libraryStored.Add(key, stored = new TrackedSet<string>(StringComparer.Ordinal));
                Add(stored, values);
            }
        }

        /// <summary>A library sequence a known call returns or a model's value creates: the iterator delegates it keeps, those no body
        /// the run has resolves, which are an unresolved dispatch wherever it is enumerated, and the sources it enumerates (R5). What it
        /// yields is recorded from its model result, alongside its element storage.</summary>
        /// <param name="region">The region.</param>
        /// <param name="creator">The creator.</param>
        /// <param name="operation">The operation.</param>
        /// <param name="nested">The nested.</param>
        private sealed class SequenceState(string region, string creator, int operation, bool nested) : ITrackedState
        {
            public string Region { get; } = region;
            public string Creator { get; } = creator;
            public int Operation { get; } = operation;
            public bool IsNested { get; } = nested;
            public TrackedSet<string> Callees { get; } = new(StringComparer.Ordinal);
            public TrackedSet<string> Yields { get; } = new(StringComparer.Ordinal);
            public TrackedSet<string> Sources { get; } = new(StringComparer.Ordinal);
            public TrackedSet<string> ReturnedSources { get; } = new(StringComparer.Ordinal);
            /// <summary>By delegate parameter and delegate region, <see cref="UNRESOLVED_DELEGATE"/> where no delegate object is known: each
            /// target of one argument is a dispatch of its own.</summary>
            public TrackedMap<(int Parameter, string Region), (string Callee, IReadOnlyList<CallArgument> Inputs, (string Region, string? DeclaringTypeKey)[] Receivers)> Unresolved { get; } = [];

            public void Attach(StateKey key, Action<StateKey> read, Action<StateKey> write)
            {
                Callees.Attach(key.Member("SequenceState.Callees"), read, write);
                Yields.Attach(key.Member("SequenceState.Yields"), read, write);
                Sources.Attach(key.Member("SequenceState.Sources"), read, write);
                ReturnedSources.Attach(key.Member("SequenceState.ReturnedSources"), read, write);
                Unresolved.Attach(key.Member("SequenceState.Unresolved"), read, write);
            }
        }

        /// <summary>The library sequence created at a call, <paramref name="path"/> telling the result from each sequence a value of its
        /// model creates.</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="call">The known call.</param>
        /// <param name="path">The sequence's path in the model.</param>
        /// <param name="nested">Whether the sequence replays only its own values.</param>
        /// <param name="destination">The output destination, or null for existing result and nested sequences.</param>
        private SequenceState NewSequence(InstanceState caller, SummaryOpaqueCall call, string path, bool nested, ResultDestination? destination = null)
        {
            var owner = _scope.Reachable.Bodies.TryGetValue(caller.BodyId, out var body) ? body.OwnerSymbol : caller.BodyId;
            var region = Region($"sequence|{caller.BodyId}#{call.OperationId}|{path}|{ContextKey(caller)}", HeapRegionKind.Allocation,
                                $"sequence:{owner}#{call.Callee}{(nested ? "@" + path : "")}", null, caller.Context,
                                $"sequence|{caller.BodyId}#{call.OperationId}|{path}", merged: caller.IsMerged,
                                site: new CreationSite(caller.BodyId, call.OperationId, destination?.TypeKey ?? call.Library!.ResultTypeKey ?? "sequence", 1),
                                modelCreationKey: destination is null ? null : $"sequence|{caller.BodyId}#{call.OperationId}|{path}");
            if (!_sequences.TryGetValue(region, out var sequence))
                _sequences.Add(region, sequence = new SequenceState(region, caller.Id, call.OperationId, nested));
            return sequence;
        }

        /// <summary>The parameters whose argument a call enumerates where it stands or keeps for a library sequence it returns: those a
        /// known call's model reads deep, writes or names the elements of, and the collection a copy of ADR 0010 copies (R5).</summary>
        /// <param name="call">The call.</param>
        private static TrackedSet<int> Consumed(SummaryOpaqueCall call)
        {
            var consumed = new TrackedSet<int>();
            if (call.IsKnown)
            {
                consumed.UnionWith(call.Library!.NamedArguments());
                consumed.UnionWith(call.Library.Effects.Select(effect => effect.ParameterOrdinal));
            }

            if (call.Collection?.Source is int source)
                consumed.Add(source);
            return consumed;
        }

        /// <summary>A consumer enumerates what it is handed where it stands, in its own execution (R5): a library sequence runs its
        /// iterator delegates and enumerates its sources there, and a user iterator is enumerated there instead of in an unknown
        /// enumeration.</summary>
        /// <param name="consumer">The consumer.</param>
        /// <param name="operationId">The operationId.</param>
        /// <param name="regions">The regions.</param>
        /// <param name="visited">The visited.</param>
        private void Consume(InstanceState consumer, int operationId, IEnumerable<string> regions, TrackedSet<string> visited)
        {
            foreach (var region in regions.ToArray())
            {
                CheckCancellation();
                if (_sequences.TryGetValue(region, out var sequence))
                {
                    if (!visited.Add(region))
                        continue;
                    foreach (var callee in sequence.Callees)
                    {
                        CheckCancellation();
                        Add(_instances[callee].Requests, consumer.Requests);
                        AddEdge((consumer.Id, operationId, callee, "delegate"));
                    }

                    // A delegate no body the run has resolves is an unresolved dispatch at each enumeration, as a call of it there would be.
                    foreach (var (callee, inputs, receivers) in sequence.Unresolved.Values)
                    {
                        CheckCancellation();
                        var invocation = new CallTransfer(operationId, callee, IrCallKind.Delegate, new TrackedSet<AbstractValue>(), inputs, []);
                        NoReceiver(consumer, invocation);
                        UnresolvedFate(consumer, invocation, receivers);
                    }

                    Consume(consumer, operationId, sequence.Sources, visited);
                }
                else if (_iteratorObjects.ContainsKey(region))
                    _rebuilding.IteratorEnumerations.Add((consumer.Id, operationId, region));
            }
        }

        private string KeeperStore(SummaryOpaqueCall call, int ordinal) => StaticRegion(call.Library!.KeeperTypeKeys[ordinal]);

        private TrackedSet<string> KeeperTargets(InstanceState caller, SummaryOpaqueCall call, int ordinal, bool read)
        {
            var targets = ordinal == IrLibraryCall.RESULT ? ResultObjects(caller, call) : Eval(caller, ArgumentOf(call, ordinal));
            if (ordinal != IrLibraryCall.RESULT && (_keptFallbacks.Contains((caller.Id, call.OperationId, ordinal)) || read && targets.Count == 0))
                targets.Add(KeeperStore(call, ordinal));
            return targets;
        }

        /// <summary>What <c>keeps.result</c> keeps into: the object the call returns, or under tasks the innermost completion value
        /// (R4).</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="call">The known call.</param>
        private TrackedSet<string> ResultObjects(InstanceState caller, SummaryOpaqueCall call)
        {
            var objects = CallResult(caller, call.OperationId).ToTrackedSet(StringComparer.Ordinal);
            for (var level = call.Library!.Result?.TaskDepth ?? call.Library.ReturnTaskDepth; level > 0; level--)
                objects = Completion(objects);
            return objects;
        }

        private static IReadOnlySet<AbstractValue> ArgumentOf(SummaryOpaqueCall call, int ordinal) =>
            ordinal == IrLibraryCall.RECEIVER ? call.Receivers :
                call.Arguments.FirstOrDefault(argument => argument.ParameterOrdinal == ordinal)?.Values ?? new TrackedSet<AbstractValue>();

        /// <summary>A call of a fated delegate at the known call's site, handed <paramref name="inputs"/>.</summary>
        /// <param name="call">The known call.</param>
        /// <param name="fate">The fate naming the delegate parameter.</param>
        /// <param name="inputs">The arguments the delegate is handed.</param>
        private static CallTransfer Invocation(SummaryOpaqueCall call, IrLibraryFate fate, IReadOnlyList<CallArgument> inputs) =>
            new(call.OperationId, call.Callee, IrCallKind.Delegate, ArgumentOf(call, fate.ParameterOrdinal), inputs, []);

        /// <summary>An <c>invoke-now</c> delegate runs as a call of it at the site would, in the caller's execution and under its locks;
        /// what it returns is no part of what the call returns unless the model's result says so.</summary>
        /// <param name="caller">The calling instance.</param>
        /// <param name="invocation">The delegate invocation.</param>
        /// <param name="callee">The instance the delegate runs.</param>
        private void RunNow(InstanceState caller, CallTransfer invocation, InstanceState callee)
        {
            if (AsyncSpawnKind(invocation, callee) is { } kind)
            {
                var site = SiteOf(_asyncSpawns, caller, invocation.OperationId, () => new SiteState(kind, invocation.OperationId));
                if (site.Callees.Add((callee.Id, SpawnRole.Work)))
                    _changes++;
            }

            AddEdge((caller.Id, invocation.OperationId, callee.Id, "delegate"));
        }

        /// <summary>What one run of each delegate a model may name the returns of gives back at a known call, and which of them may give
        /// back an object the heap does not follow: the one answer every model value naming <c>returns:</c> reads (R1, R2, R4).</summary>
        /// <param name="Values">The objects each delegate parameter's runs give back, by parameter ordinal.</param>
        /// <param name="Unfollowed">The delegate parameters some run of which may give back such an object.</param>
        private sealed record DelegateReturns(IReadOnlyDictionary<int, TrackedSet<string>> Values, IReadOnlySet<int> Unfollowed)
        {
            public IEnumerable<string> Of(int ordinal) => Values.GetValueOrDefault(ordinal) ?? [];
        }

        /// <summary>What one run of each <c>invoke-now</c> or <c>iterator</c> delegate gives back: a non-async body what it returns, an
        /// async body its task, a region of the call and the parameter that completes with what the body returns, as the task of an async
        /// call does (R1). A delegate gives back an object the heap does not follow where a run of it may, or where some alternative of it
        /// runs code the analysis does not see in full (R2).</summary>
        /// <param name="caller">The instance making the known call.</param>
        /// <param name="call">The known call.</param>
        /// <param name="runs">The delegate regions and the instances they run, by parameter ordinal.</param>
        /// <param name="unseen">The delegate parameters some alternative of which runs code the analysis does not see in full.</param>
        private DelegateReturns ReturnsOf(InstanceState caller, SummaryOpaqueCall call, IReadOnlyDictionary<int, TrackedList<(string Region, InstanceState Callee)>> runs,
                                          IReadOnlySet<int> unseen)
        {
            var values = new TrackedMap<int, TrackedSet<string>>();
            var unfollowed = new HashSet<int>();
            foreach (var fate in call.Library!.Fates.Where(fate => fate.Kind is IrFateKind.InvokeNow or IrFateKind.Iterator))
            {
                CheckCancellation();
                var returned = new TrackedSet<string>(StringComparer.Ordinal);
                var site = new DelegateSite(caller.Id, call.OperationId, fate.ParameterOrdinal);
                _rebuilding.DelegateSites.Add(site);
                foreach (var (_, callee) in runs[fate.ParameterOrdinal])
                {
                    CheckCancellation();
                    if (IsAsyncBody(callee.BodyId) && _scope.Reachable.Bodies[callee.BodyId].ReturnType != "void")
                    {
                        var task = ReturnedTask(caller, call.OperationId, fate.ParameterOrdinal);
                        Complete(task, callee.Returns, callee.ReturnsUnfollowed, new TaskCompleter(callee.Id, -1, TaskCompleterKind.Returns));
                        returned.Add(task);
                        _rebuilding.DelegateRuns.Add((site, new DelegateRun(callee.Id, task)));
                    }
                    else
                    {
                        returned.UnionWith(callee.Returns);
                        _rebuilding.DelegateRuns.Add((site, new DelegateRun(callee.Id, null)));
                    }
                }

                values[fate.ParameterOrdinal] = returned;
                if (unseen.Contains(fate.ParameterOrdinal))
                    _rebuilding.UnseenDelegates.Add(site);
                if (unseen.Contains(fate.ParameterOrdinal) || runs[fate.ParameterOrdinal].Any(run => run.Callee.ReturnsUnfollowed))
                    unfollowed.Add(fate.ParameterOrdinal);
            }

            return new DelegateReturns(values, unfollowed);
        }

        /// <summary>The task an async delegate's run gives back at a known call, one per call, delegate parameter and context.</summary>
        /// <param name="instance">The instance making the call.</param>
        /// <param name="operationId">The call's operation.</param>
        /// <param name="ordinal">The delegate parameter's ordinal.</param>
        private string ReturnedTask(InstanceState instance, int operationId, int ordinal)
        {
            var owner = _scope.Reachable.Bodies.TryGetValue(instance.BodyId, out var body) ? body.OwnerSymbol : instance.BodyId;
            return Region($"task|{instance.BodyId}#{operationId}^returns{ordinal}|{ContextKey(instance)}", HeapRegionKind.Task,
                          $"task:{owner}#{operationId}^returns{ordinal}", null, instance.Context, $"task|{instance.BodyId}#{operationId}^returns{ordinal}",
                          merged: instance.IsMerged, site: new CreationSite(instance.BodyId, operationId, "", 0));
        }

        /// <summary>What a known call enumerates of what its delegates give back: what the delegates whose returns it names the elements
        /// of returned, and what the tasks complete with that a completion names the elements of, with each value holding those tasks,
        /// where they are no argument's, the receiver's or a keeper's, which the call's own effects enumerate
        /// (<see cref="CollectionObjects.LibraryEffects"/>) (R2, R5).</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="call">The known call.</param>
        /// <param name="ordinals">The delegate parameters whose returns are enumerated.</param>
        /// <param name="completions">The completions whose values are enumerated.</param>
        /// <param name="returns">What the call's delegates give back.</param>
        private IEnumerable<string> EnumeratedReturned(InstanceState caller, SummaryOpaqueCall call, IEnumerable<int> ordinals,
                                                       IEnumerable<IrModelCompletion> completions, DelegateReturns returns) =>
            ordinals.SelectMany(returns.Of)
                    .Concat(completions.Where(completion => !IrLibraryCall.ReachesCallValues(completion))
                                       .SelectMany(completion => CompletionSources(caller, call, completion, returns)))
                    .ToArray();

        /// <summary>What enumerating a completion's values enumerates: what the tasks it names complete with, and each value holding those
        /// tasks, as elements(…) of it would (R2).</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="call">The known call.</param>
        /// <param name="completion">The completion.</param>
        /// <param name="returns">What the call's delegates give back.</param>
        private IEnumerable<string> CompletionSources(InstanceState caller, SummaryOpaqueCall call, IrModelCompletion completion, DelegateReturns returns)
        {
            var (source, depth) = IrLibraryCall.CompletedSource(completion);
            return IrLibraryCall.CompletionHolders(completion).SelectMany(holder => ModelValues(caller, call, holder, returns, SOURCE_PATH))
                                .Concat(HeapSolution.Completions(ModelValues(caller, call, source, returns, SOURCE_PATH), depth, task => Completion([task])))
                                .ToArray();
        }

        /// <summary>An unresolved dispatch of a fated delegate, which sees what the delegate is handed (R1).</summary>
        /// <param name="caller">The caller.</param>
        /// <param name="invocation">The invocation.</param>
        /// <param name="receivers">The receivers.</param>
        private void UnresolvedFate(InstanceState caller, CallTransfer invocation, IReadOnlyCollection<(string Region, string? DeclaringTypeKey)> receivers)
        {
            Unresolved(caller, invocation, receivers);
            if (!_rebuilding.UnresolvedFateInputs.TryGetValue((caller.Id, invocation.OperationId), out var seen))
                _rebuilding.UnresolvedFateInputs.Add((caller.Id, invocation.OperationId), seen = new TrackedSet<string>(StringComparer.Ordinal));
            seen.UnionWith(Eval(caller, invocation.Arguments.SelectMany(argument => argument.Values)));
        }

        /// <summary>The objects a value of a model names at a known call: what an argument points to, what a delegate returned, what
        /// enumerating either yields, or a new library sequence or grouping built of values, created at the call; <paramref name="path"/>
        /// tells apart each such object one call creates.</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="call">The known call.</param>
        /// <param name="value">The model value to evaluate.</param>
        /// <param name="returns">What each named delegate gives back (<see cref="ReturnsOf"/>).</param>
        /// <param name="path">The value's path within this call's model.</param>
        private TrackedSet<string> ModelValues(InstanceState caller, SummaryOpaqueCall call, IrModelValue value, DelegateReturns returns,
                                            string path)
        {
            switch (value)
            {
                case IrModelThis:
                    return Eval(caller, call.Receivers);
                case IrModelNew created:
                    return [ModelNew(caller, call.OperationId, created, path)];
                case IrModelKept kept:
                {
                    var keepers = KeeperTargets(caller, call, kept.KeeperOrdinal, read: true).ToArray();
                    // A keeper keeping an unfollowed completion value delivers it through this channel as that completion would (R2).
                    if (path != SOURCE_PATH && keepers.Any(_unfollowedKept.Contains) && caller.UnfollowedModelCompletions.Add((call.OperationId, Channel(path))))
                        _changes++;
                    return keepers.SelectMany(keeper => Load(keeper, PathValue.KEPT)).ToTrackedSet(StringComparer.Ordinal);
                }
                case IrModelArgument argument:
                    return Eval(caller, ArgumentOf(call, argument.ParameterOrdinal));
                case IrModelReturns returned:
                    return new TrackedSet<string>(returns.Of(returned.ParameterOrdinal), StringComparer.Ordinal);
                case IrModelElements elements:
                    return Elements(ModelValues(caller, call, elements.Source, returns, path + ".e"), elements.ElementTypeKey);
                case IrModelSequence sequenceValue:
                {
                    // A sequence a value builds yields its values and enumerates the arguments they name the elements of (R5).
                    var sequence = NewSequence(caller, call, path, nested: true);
                    AddSequenceYields(sequence,
                        sequenceValue.Values.SelectMany((item, position) => ModelValues(caller, call, item, returns, $"{path}.{position}")));
                    AddSources(caller, call, sequence, sequenceValue.Values, returns);
                    AddReturnedSources(sequence, EnumeratedReturned(caller, call, IrLibraryCall.EnumeratedReturns(sequenceValue.Values),
                                                                    IrLibraryCall.EnumeratedCompletions(sequenceValue.Values), returns));
                    return [sequence.Region];
                }
                case IrModelGrouping groupingValue:
                {
                    var owner = _scope.Reachable.Bodies.TryGetValue(caller.BodyId, out var body) ? body.OwnerSymbol : caller.BodyId;
                    var grouping = Region($"grouping|{caller.BodyId}#{call.OperationId}|{path}|{ContextKey(caller)}", HeapRegionKind.Allocation,
                                          $"grouping:{owner}#{call.Callee}@{path}", null, caller.Context,
                                          $"grouping|{caller.BodyId}#{call.OperationId}|{path}", merged: caller.IsMerged,
                                          site: new CreationSite(caller.BodyId, call.OperationId, "grouping", 1));
                    if (_groupings.Add(grouping))
                        _changes++;
                    Add(Field(grouping, PathValue.KEYS), ModelValues(caller, call, groupingValue.Key, returns, path + ".k"));
                    Add(Field(grouping, PathValue.ELEMENT), ModelValues(caller, call, groupingValue.Values, returns, path + ".v"));
                    return [grouping];
                }
                case IrModelHolderArgument:
                    return new TrackedSet<string>(StringComparer.Ordinal);
                case IrModelCompletion completion:
                {
                    // What the tasks the source names complete with (R4). Where one of them may complete with an object the heap does not
                    // follow, or the source may itself name one, as a task argument or what a delegate gives back may, this channel's value
                    // may be such an object, and no other's (R2).
                    var tasks = ModelValues(caller, call, completion.Source, returns, path + ".c");
                    if ((tasks.Any(_unfollowedCompletions.Contains) || ModelUnfollowed(caller, call, completion.Source, returns)) &&
                        caller.UnfollowedModelCompletions.Add((call.OperationId, Channel(path))))
                        _changes++;
                    return Completion(tasks);
                }
                default:
                    throw new System.Diagnostics.UnreachableException($"Unknown value kind {value.GetType().Name}.");
            }
        }

        /// <summary>The channel a model value's path stands in, which answers for its own <c>completion(…)</c> values: the result, an
        /// output, a keeper or a store target by its first segment, a fate input by its parameter and index.</summary>
        /// <param name="path">The value's path within its call's model.</param>
        private static string Channel(string path)
        {
            var segments = path.Split('.');
            return segments[0].StartsWith("fate", StringComparison.Ordinal) && segments.Length > 1 ? $"{segments[0]}.{segments[1]}" : segments[0];
        }

        /// <summary>Whether a model value at a known call may name an object the heap does not follow, as the same value handed over
        /// directly would: an argument by its own sources, source calls and completions, what a delegate gives back as <see cref="ReturnsOf"/> answers, what a
        /// keeper keeps by what was put there, and a value built of others by any of them. A task completing with it carries the answer
        /// (R2). A <c>completion(…)</c> value answers through its channel, which <see cref="ModelValues"/> marks; <c>this</c>, <c>new</c>
        /// and a holder argument are followed.</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="call">The known call.</param>
        /// <param name="value">The model value.</param>
        /// <param name="returns">What the call's delegates give back, and which may give back such an object.</param>
        private bool ModelUnfollowed(InstanceState caller, SummaryOpaqueCall call, IrModelValue value, DelegateReturns returns) => value switch
        {
            IrModelArgument argument => call.Arguments.Any(candidate => candidate.ParameterOrdinal == argument.ParameterOrdinal &&
                                                                        Unfollowed(caller, candidate.UnknownSources, candidate.SourceCalls, candidate.Completions)),
            IrModelReturns returned => returns.Unfollowed.Contains(returned.ParameterOrdinal),
            IrModelKept kept => KeeperTargets(caller, call, kept.KeeperOrdinal, read: true)
                .Any(keeper => _unfollowedKept.Contains(keeper) || _unfollowedKeptValues.Contains(keeper)),
            IrModelElements elements => ModelUnfollowed(caller, call, elements.Source, returns),
            IrModelSequence sequence => sequence.Values.Any(item => ModelUnfollowed(caller, call, item, returns)),
            IrModelGrouping grouping => ModelUnfollowed(caller, call, grouping.Key, returns) ||
                                        ModelUnfollowed(caller, call, grouping.Values, returns),
            _ => false
        };

        /// <summary>A fresh object a model hands to one delegate parameter at one call site.</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="operationId">The call operation that runs the delegate.</param>
        /// <param name="value">The fresh input and its declared type.</param>
        /// <param name="path">The fate and input position distinguishing this object.</param>
        private string ModelNew(InstanceState caller, int operationId, IrModelNew value, string path)
        {
            var typeKey = ProgramIndex.Substitute(value.TypeKey, caller.Substitution);
            var owner = _scope.Reachable.Bodies.TryGetValue(caller.BodyId, out var body) ? body.OwnerSymbol : caller.BodyId;
            var key = $"model-new|{caller.BodyId}#{operationId}|{path}|{typeKey}";
            return Region($"{key}|{ContextKey(caller)}", HeapRegionKind.Allocation, $"alloc:{owner}#{DisplayType(typeKey)}", typeKey,
                          caller.Context, key, merged: caller.IsMerged, site: new CreationSite(caller.BodyId, operationId, value.TypeKey, 1),
                          modelCreationKey: key);
        }

        /// <summary>The arguments a sequence's values name the elements of are the sources it enumerates.</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="call">The modelled call.</param>
        /// <param name="sequence">The sequence keeping these sources.</param>
        /// <param name="values">The model values naming their elements.</param>
        /// <param name="returns">What the call's delegates give back (<see cref="ReturnsOf"/>).</param>
        private void AddSources(InstanceState caller, SummaryOpaqueCall call, SequenceState sequence, IEnumerable<IrModelValue> values, DelegateReturns returns)
        {
            foreach (var kept in IrLibraryCall.EnumeratedKeepers(values))
            {
                CheckCancellation();
                foreach (var source in ModelValues(caller, call, kept, returns, SOURCE_PATH))
                {
                CheckCancellation();
                    if (sequence.Sources.Add(source))
                    _changes++;
            }
            }
            foreach (var ordinal in IrLibraryCall.EnumeratedArguments(values))
            {
                CheckCancellation();
                foreach (var source in Eval(caller, ArgumentOf(call, ordinal)))
                {
                    CheckCancellation();
                    if (sequence.Sources.Add(source))
                        _changes++;
                }
            }
            // What the tasks a completion names complete with is a source as that value handed over directly would be (R2).
            foreach (var completion in IrLibraryCall.EnumeratedCompletions(values))
            {
                CheckCancellation();
                foreach (var completed in CompletionSources(caller, call, completion, returns))
                {
                    CheckCancellation();
                    if (sequence.Sources.Add(completed))
                        _changes++;
                }
            }
        }

        /// <summary>What a delegate returned that a sequence's values name the elements of is a source it enumerates too (R5).</summary>
        /// <param name="sequence">The sequence.</param>
        /// <param name="returned">The returned.</param>
        private void AddReturnedSources(SequenceState sequence, IEnumerable<string> returned)
        {
            foreach (var source in returned)
            {
                CheckCancellation();
                sequence.ReturnedSources.Add(source);
                if (sequence.Sources.Add(source))
                    _changes++;
            }
        }

        private void AddSequenceYields(SequenceState sequence, IEnumerable<string> values)
        {
            var yielded = values.ToArray();
            Add(Field(sequence.Region, PathValue.ELEMENT), yielded);
            Add(sequence.Yields, yielded);
        }

        /// <summary>What enumerating objects yields (R3, R5): what a library sequence or a grouping yields, what an array's or a
        /// collection's storages hold, and for any other sequence of the run's own every object it reaches of the element type, or every
        /// one where that type says nothing (SPEC TD-034a). A user iterator yields what its body's <c>yield return</c>s hand out.</summary>
        /// <param name="sources">The sources.</param>
        /// <param name="elementTypeKey">The elementTypeKey.</param>
        private TrackedSet<string> Elements(IEnumerable<string> sources, string? elementTypeKey)
        {
            var yielded = new TrackedSet<string>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                CheckCancellation();
                var type = _regions[source].TypeKey;
                if (_iteratorObjects.TryGetValue(source, out var iterator))
                    yielded.UnionWith(_instances.GetValueOrDefault(iterator.CalleeInstance)?.Yields ?? []);
                else if (_sequences.ContainsKey(source) || _groupings.Contains(source))
                    yielded.UnionWith(Load(source, PathValue.ELEMENT));
                else if (InterproceduralAccesses.IsCollectionType(_program, type))
                    yielded.UnionWith(Load(source, PathValue.ELEMENT).Concat(Load(source, PathValue.KEYS)));
                else if (type is not null && _program.Type(type) is { IsSource: true })
                {
                    yielded.UnionWith(Closure(source).Where(reached => _regions[reached].Kind != HeapRegionKind.Delegate &&
                                                                       (elementTypeKey is null || _regions[reached].TypeKey is { } reachedType &&
                                                                        _program.Supertypes(reachedType).Contains(elementTypeKey, StringComparer.Ordinal))));
                }
            }

            return yielded;
        }

        /// <summary>The result or output parameter receiving a model's result form.</summary>
        /// <param name="OutputOrdinal">The output parameter ordinal, or null for the call's result.</param>
        /// <param name="TypeKey">The destination's referenced type key.</param>
        private sealed record ResultDestination(int? OutputOrdinal, string? TypeKey)
        {
            public string Path => OutputOrdinal is int ordinal ? $"output{ordinal}" : "result";
        }

        /// <summary>Assigns the objects a model's result form names or creates to its result or output destination.</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="call">The known call whose result is evaluated.</param>
        /// <param name="form">The model's result form, its leaf under any depth of <c>task(…)</c>.</param>
        /// <param name="returns">What the model's delegates give back, and which may give back an object the heap does not follow.</param>
        /// <param name="sequence">The sequence created for a sequence result.</param>
        /// <param name="destination">The result or output parameter receiving the objects.</param>
        private void ModelResult(InstanceState caller, SummaryOpaqueCall call, IrLibraryResult form, DelegateReturns returns,
                                 SequenceState? sequence, ResultDestination destination)
        {
            // Under task(…) the leaf is what the innermost task completes with (R4).
            var result = form.Leaf;
            var values = result.Values.Select((value, position) => ModelValues(caller, call, value, returns, $"{destination.Path}.{position}")).ToArray();
            // A task completing with a oneOf leaf completes unfollowed where any of its values is, as that value handed over directly would
            // be (R2); a new object, collection, dictionary or sequence the call creates is followed whatever it is built of, as its
            // synchronous twin is. With no task around it, a synchronous result keeps no unknown source of what it names but a
            // completion(…)'s, as at the start commit.
            var target = destination.OutputOrdinal is int ordinal ? RefResult(caller, call.OperationId, ordinal)
                : ResultSlot(caller, call, form.TaskDepth,
                             caller.UnfollowedModelCompletions.Contains((call.OperationId, destination.Path)) ||
                             form.TaskDepth > 0 && result.Kind == IrResultKind.OneOf &&
                             result.Values.Any(value => ModelUnfollowed(caller, call, value, returns)));
            switch (result.Kind)
            {
                case IrResultKind.Sequence:
                    if (sequence is null)
                        throw new System.Diagnostics.UnreachableException("Sequence result has no sequence.");
                    AddSequenceYields(sequence, values.SelectMany(value => value));
                    AddSources(caller, call, sequence, result.Values, returns);
                    AddReturnedSources(sequence, EnumeratedReturned(caller, call, IrLibraryCall.EnumeratedReturns(result.Values),
                                                                    IrLibraryCall.EnumeratedCompletions(result.Values), returns));
                    Add(target, [sequence.Region]);
                    break;
                case IrResultKind.OneOf:
                    Add(target, values.SelectMany(value => value));
                    break;
                case IrResultKind.New:
                    if (destination.TypeKey is { } graphType)
                        Add(target, [NewGraph(caller, call, destination, graphType)]);
                    break;
                case IrResultKind.Collection:
                case IrResultKind.Dictionary:
                    if (destination.TypeKey is not { } typeKey)
                        return;
                    var created = destination.OutputOrdinal is null ? CreatedObject(caller, call, typeKey)
                                                                   : CreatedForDestination(caller, call, typeKey, destination, "root");
                    Add(target, [created]);
                    if (result.Kind == IrResultKind.Dictionary)
                    {
                        Add(Field(created, PathValue.KEYS), values[0]);
                        Add(Field(created, PathValue.ELEMENT), values[1]);
                    }
                    else
                        Add(Field(created, PathValue.ELEMENT), values.SelectMany(value => value));
                    break;
                default:
                    throw new System.Diagnostics.UnreachableException($"Unknown result kind {result.Kind}.");
            }

        }

        /// <summary>Where what a known call returns lands: the call's result, or for a result of <paramref name="depth"/> tasks the
        /// completion slot of the innermost of a chain of task regions — the call's own task, completed with a task of its own for each
        /// further level, each a region of its own (R4). With no task around it, what lands is the call's own completion value: the
        /// summary records the call among the completions of its result (<see cref="SummaryValue.Completions"/>), and an unfollowed one
        /// marks it as an await of an unfollowed task is marked, which every reader of <c>Unfollowed</c> sees (R2).</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="call">The known call.</param>
        /// <param name="depth">How many tasks stand around what lands.</param>
        /// <param name="unfollowed">Whether what lands in the innermost task may be an object the heap does not follow.</param>
        private TrackedSet<string> ResultSlot(InstanceState caller, SummaryOpaqueCall call, int depth, bool unfollowed)
        {
            if (depth == 0)
            {
                if (unfollowed && caller.UnfollowedCompletions.Add(call.OperationId))
                    _changes++;
                return CallResult(caller, call.OperationId);
            }
            var task = TaskRegion(caller, call.OperationId);
            Add(CallResult(caller, call.OperationId), [task]);
            for (var level = 1; level < depth; level++)
            {
                CheckCancellation();
                var inner = TaskRegion(caller, call.OperationId, level);
                Complete(task, [inner], unfollowed: false, new TaskCompleter(caller.Id, call.OperationId, TaskCompleterKind.Task));
                task = inner;
            }

            Complete(task, [], unfollowed, new TaskCompleter(caller.Id, call.OperationId, TaskCompleterKind.ModelResult));
            return Field(task, COMPLETION_FIELD);
        }

        /// <summary>Creates an object whose identity, group and display distinguish its destination and path in the call's graph.</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="call">The known call.</param>
        /// <param name="declaredType">The destination's referenced type key.</param>
        /// <param name="destination">The result or output destination.</param>
        /// <param name="path">The path from that destination's root.</param>
        private string CreatedForDestination(InstanceState caller, SummaryOpaqueCall call, string declaredType, ResultDestination destination, string path)
        {
            var typeKey = ProgramIndex.Substitute(declaredType, caller.Substitution);
            var owner = _scope.Reachable.Bodies.TryGetValue(caller.BodyId, out var body) ? body.OwnerSymbol : caller.BodyId;
            var key = $"model|{caller.BodyId}#{call.OperationId}|{destination.Path}|{path}|{typeKey}";
            var ordinal = ModelCreationOrdinal(caller, call, typeKey, destination, path);
            return Region($"{key}|{ContextKey(caller)}", HeapRegionKind.Allocation,
                          $"alloc:{owner}#{DisplayType(typeKey)}{(ordinal > 1 ? "#" + ordinal : "")}", typeKey, caller.Context, key,
                          merged: caller.IsMerged, site: new CreationSite(caller.BodyId, call.OperationId, declaredType, ordinal), modelCreationKey: key);
        }

        private int ModelCreationOrdinal(InstanceState caller, SummaryOpaqueCall call, string typeKey, ResultDestination destination, string path)
        {
            if (!_scope.Reachable.Bodies.TryGetValue(caller.BodyId, out var body))
                return 1;
            var bodies = _scope.Reachable.Bodies.Values.Where(candidate => candidate.OwnerSymbol == body.OwnerSymbol).ToArray();
            var explicitCount = bodies.SelectMany(candidate => candidate.Blocks.SelectMany(block => block.Operations)).OfType<IrAllocateOperation>()
                                      .Where(allocation => ProgramIndex.Substitute(allocation.AllocatedTypeKey ?? allocation.AllocatedType, caller.Substitution) == typeKey)
                                      .DistinctBy(allocation => (allocation.Provenance.Span, allocation.AllocatedTypeKey ?? allocation.AllocatedType)).Count();
            var preceding = 0;
            foreach (var (candidate, operation) in bodies.SelectMany(candidate => candidate.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
                .Where(operation => operation.Library is { InRange: true, DeclaredOpaque: false }).Select(operation => (candidate, operation))))
            {
                CheckCancellation();
                var span = operation.Provenance.Span;
                var here = call.Provenance!.Span;
                var order = StringComparer.Ordinal.Compare(span.Path, here.Path);
                if (order == 0)
                    order = span.StartLine.CompareTo(here.StartLine);
                if (order == 0)
                    order = span.StartColumn.CompareTo(here.StartColumn);
                if (order == 0)
                    order = StringComparer.Ordinal.Compare(candidate.BodyId, caller.BodyId);
                if (order == 0)
                    order = operation.Id.CompareTo(call.OperationId);
                var before = order < 0;
                var same = order == 0;
                if (!before && !same)
                    continue;
                var model = operation.Library!;
                if (model is { Result: { } result, ResultTypeKey: { } resultType })
                    Count(result, new ResultDestination(null, resultType));
                foreach (var output in model.Outputs.OrderBy(output => output.Key))
                {
                    CheckCancellation();
                    Count(output.Value, new ResultDestination(output.Key, model.OutputTypeKeys[output.Key]));
                }

                void Count(IrLibraryResult form, ResultDestination other)
                {
                    var earlierDestination = destination.OutputOrdinal is int ordinal && (other.OutputOrdinal is null || other.OutputOrdinal < ordinal);
                    var sameDestination = other.OutputOrdinal == destination.OutputOrdinal;
                    if (!before && !earlierDestination && !sameDestination)
                        return;
                    foreach (var node in CreatedNodes(form, ProgramIndex.Substitute(other.TypeKey!, caller.Substitution), other))
                    {
                        CheckCancellation();
                        if (!before && sameDestination && node.Path == path)
                            break;
                        if (node.TypeKey == typeKey)
                            preceding++;
                    }
                }
            }
            return 1 + explicitCount + preceding;
        }

        private sealed record GraphNode(string Path, string TypeKey, string? Parent, string? Slot, int Depth);

        private IReadOnlyList<GraphNode> CreatedNodes(IrLibraryResult form, string typeKey, ResultDestination destination) => form.Leaf.Kind switch
        {
            IrResultKind.New => GraphNodes(typeKey),
            IrResultKind.Collection or IrResultKind.Dictionary when destination.OutputOrdinal is not null => [new GraphNode("root", typeKey, null, null, 0)],
            _ => []
        };

        private IReadOnlyList<GraphNode> GraphNodes(string typeKey)
        {
            CheckCancellation();
            var nodes = new TrackedList<GraphNode> { new("root", typeKey, null, null, 0) };
            for (var index = 0; index < nodes.Count; index++)
            {
                CheckCancellation();
                var node = nodes[index];
                if (node.Depth >= _limits.MaxAccessPathDepth)
                    continue;
                if (IsSourceClass(node.TypeKey))
                {
                    foreach (var field in _program.InstanceFieldsOf(node.TypeKey) ?? [])
                    {
                        CheckCancellation();
                        if (field.FieldTypeKey is { } fieldType && (IsSourceClass(fieldType) || IsGraphCollection(fieldType)))
                            Child(fieldType, FieldSlot.Key(field));
                    }
                }
                if (GraphCollectionType(node.TypeKey) is { } collectionType)
                {
                    if (collectionType.EndsWith(']'))
                    {
                        var element = collectionType[..collectionType.LastIndexOf('[')];
                        if (IsSourceClass(element))
                            Child(element, PathValue.ELEMENT);
                    }
                    else
                    {
                        var arguments = ProgramIndex.Shape(collectionType).Arguments;
                        if (InterproceduralAccesses.IsDictionaryType(_program, collectionType) && arguments.Count == 2 && IsSourceClass(arguments[0]))
                            Child(arguments[0], PathValue.KEYS);
                        if (arguments.Count != 0 && IsSourceClass(arguments[^1]))
                            Child(arguments[^1], PathValue.ELEMENT);
                    }
                }

                void Child(string childType, string slot) => nodes.Add(new GraphNode(node.Path + "/" + slot, childType, node.Path, slot, node.Depth + 1));
            }
            return nodes;
        }

        private bool IsSourceClass(string typeKey) => _program.Type(typeKey) is
            { IsSource: true, IsInterface: false, IsAbstract: false, IsValueType: false, IsDelegate: false };

        private bool IsGraphCollection(string typeKey) => GraphCollectionType(typeKey) is not null;

        private string? GraphCollectionType(string typeKey) =>
            _program.Type(typeKey) is { IsInterface: true } or { IsAbstract: true } or { IsValueType: true } ? null :
            _program.Supertypes(typeKey).FirstOrDefault(key => _program.Type(key) is not { IsSource: true } &&
                                                            InterproceduralAccesses.IsConcreteCollectionType(_program, key));

        /// <summary>Creates the destination's new graph in breadth-first field and element order, without making accesses.</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="call">The known call.</param>
        /// <param name="destination">The result or output parameter receiving the graph.</param>
        /// <param name="declaredType">The destination's referenced type key before caller substitution.</param>
        private string NewGraph(InstanceState caller, SummaryOpaqueCall call, ResultDestination destination, string declaredType)
        {
            var objects = new TrackedMap<string, string>(StringComparer.Ordinal);
            foreach (var node in GraphNodes(ProgramIndex.Substitute(declaredType, caller.Substitution)))
            {
                CheckCancellation();
                var created = CreatedForDestination(caller, call, node.TypeKey, destination, node.Path);
                objects.Add(node.Path, created);
                if (node.Parent is { } parent)
                    Add(Field(objects[parent], node.Slot!), [created]);
            }
            return objects["root"];
        }

        /// <summary>The new object of <paramref name="resultType"/> a call creates at its site and returns.</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="call">The call.</param>
        /// <param name="resultType">The object's type key before the caller's substitution.</param>
        private string CreatedAtCall(InstanceState caller, SummaryOpaqueCall call, string resultType)
        {
            var created = CreatedObject(caller, call, resultType);
            Add(CallResult(caller, call.OperationId), [created]);
            return created;
        }

        /// <summary>The new object of <paramref name="resultType"/> a call creates at its site, wherever its result puts it.</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="call">The call.</param>
        /// <param name="resultType">The object's type key before the caller's substitution.</param>
        private string CreatedObject(InstanceState caller, SummaryOpaqueCall call, string resultType)
        {
            var typeKey = ProgramIndex.Substitute(resultType, caller.Substitution);
            var owner = _scope.Reachable.Bodies.TryGetValue(caller.BodyId, out var body) ? body.OwnerSymbol : caller.BodyId;
            var created = Region($"alloc|{caller.BodyId}#{call.OperationId}|{typeKey}|{ContextKey(caller)}", HeapRegionKind.Allocation,
                                 $"alloc:{owner}#{DisplayType(typeKey)}", typeKey, caller.Context, $"alloc|{caller.BodyId}#{call.OperationId}",
                                 merged: caller.IsMerged, site: new CreationSite(caller.BodyId, call.OperationId, resultType, 1),
                                 exactType: call.IsConstructor);
            return created;
        }

        private const string UNRESOLVED_DELEGATE = "";

        /// <summary>Looks up a reference target's locations in its instance. Calls and parameters carry locations through the fixpoint.</summary>
        /// <param name="instance">The instance in whose frame the target is resolved.</param>
        /// <param name="target">The reference target to resolve.</param>
        private IEnumerable<(string Region, string Slot)> ReferenceLocations(InstanceState instance, ReferenceTarget target)
        {
            CheckCancellation();
            // The counter counts the solve's lookups; a query of the solved heap is not one.
            if (!Querying)
                _counters[HeapCounters.REFERENCE_LOOKUPS]++;
            switch (target)
            {
                case ReferenceCell cell:
                {
                    var owners = cell.Field.IsStatic ? new TrackedSet<string>(StringComparer.Ordinal) { StaticRegion(instance, cell.Field) } : Eval(instance, cell.Bases);
                    foreach (var owner in owners)
                    {
                        CheckCancellation();
                        if (cell.IsOnCollection)
                        {
                            foreach (var array in Load(owner, FieldSlot.Key(cell.Field)))
                            {
                                CheckCancellation();
                                yield return (array, PathValue.ELEMENT);
                            }
                        }
                        else
                            yield return (owner, FieldSlot.Key(cell.Field));
                    }
                    break;
                }
                case ReferenceParameter parameter:
                    foreach (var location in ParameterLocations(instance, parameter.Ordinal).ToArray())
                    {
                        CheckCancellation();
                        yield return location;
                    }
                    break;
                case ReferenceParameterElement element:
                    foreach (var array in Parameter(instance, element.Ordinal).ToArray())
                    {
                        CheckCancellation();
                        yield return (array, PathValue.ELEMENT);
                    }
                    break;
                case ReferenceCallCollection collection:
                    foreach (var array in CallResult(instance, collection.OperationId).ToArray())
                    {
                        CheckCancellation();
                        yield return (array, PathValue.ELEMENT);
                    }
                    break;
                case ReferenceCall reference:
                    foreach (var edge in _edgesByCall.GetValueOrDefault((instance.Id, reference.OperationId))?.ToArray() ?? [])
                    {
                        CheckCancellation();
                        var callee = _instances[edge.Callee];
                        foreach (var location in callee.ReturnedLocations.ToArray())
                        {
                            CheckCancellation();
                            yield return location;
                        }
                    }
                    break;
                case ReferenceUnproven:
                    break;
                default:
                    throw new System.Diagnostics.UnreachableException($"Unknown reference target {target.GetType().Name}.");
            }
        }

        /// <summary>What a holder keeps of one delegate: what each of its parameters is handed besides the arguments of the member
        /// call that runs it, and the indices of those arguments (R4).</summary>
        private sealed class HeldDelegate : ITrackedState
        {
            /// <summary>The call that made the delegate held, which names its unresolved dispatch where it escapes.</summary>
            private readonly TrackedValue<string> _callee = new("");
            public string Callee { get => _callee.Value; init => _callee.Value = value; }
            public TrackedList<TrackedSet<string>> Regions { get; } = [];
            public TrackedList<TrackedSet<int>> HolderArguments { get; } = [];
            public TrackedList<TrackedSet<HeldNewInput>> NewInputs { get; } = [];

            public void Attach(StateKey key, Action<StateKey> read, Action<StateKey> write)
            {
                Regions.Attach(key.Member("HeldDelegate.Regions"), read, write);
                HolderArguments.Attach(key.Member("HeldDelegate.HolderArguments"), read, write);
                NewInputs.Attach(key.Member("HeldDelegate.NewInputs"), read, write);
                _callee.Attach(key.Member("HeldDelegate.Callee"), read, write);
            }
        }

        /// <summary>A fresh input retained until a holder member runs its delegate.</summary>
        /// <param name="FatePlace">The original model member and call site that registered this fate.</param>
        /// <param name="FateParameterOrdinal">The library member's delegate parameter.</param>
        /// <param name="Position">The value's position in the delegate parameter input.</param>
        /// <param name="TypeKey">The delegate parameter's type key.</param>
        private sealed record HeldNewInput(string FatePlace, int FateParameterOrdinal, int Position, string TypeKey)
        {
            /// <summary>The unique place of this new value within the original fate.</summary>
            /// <param name="ordinal">The delegate input parameter ordinal.</param>
            public string Path(int ordinal) => $"{FatePlace}|fate{FateParameterOrdinal}.{ordinal}.{Position}";
        }

        /// <summary>A <c>holder</c> fate keeps the delegate in its holder: a new object created at the call, which the call returns, or
        /// for a constructor the object it creates, or each object the receiver points to. A receiver the heap knows no object for keeps
        /// it where the analysis cannot follow it, so it runs in an unknown execution (R4).</summary>
        /// <param name="caller">The caller.</param>
        /// <param name="call">The call.</param>
        /// <param name="fate">The fate.</param>
        /// <param name="returns">What the call's delegates give back (<see cref="ReturnsOf"/>).</param>
        private void Hold(InstanceState caller, SummaryOpaqueCall call, IrLibraryFate fate, DelegateReturns returns)
        {
            var regions = fate.Inputs.Select((input, ordinal) => input.Select((value, position) => (Value: value, Position: position))
                                                                      .Where(item => item.Value is not (IrModelHolderArgument or IrModelNew))
                                                                      .SelectMany(item => ModelValues(caller, call, item.Value, returns,
                                                                                                      $"fate{fate.ParameterOrdinal}.{ordinal}.{item.Position}"))
                                                                      .ToTrackedSet(StringComparer.Ordinal))
                              .ToArray();
            var holderArguments = fate.Inputs.Select(input => input.OfType<IrModelHolderArgument>().Select(argument => argument.Index).ToTrackedSet()).ToArray();
            var newInputs = fate.Inputs.Select(input => input.Select((value, position) => (value, position))
                                                            .Where(item => item.value is IrModelNew)
                                                            .Select(item => new HeldNewInput($"{call.Callee}|{caller.Id}#{call.OperationId}",
                                                                                           fate.ParameterOrdinal, item.position,
                                                                                           ((IrModelNew)item.value).TypeKey)).ToTrackedSet())
                                       .ToArray();
            var delegates = Eval(caller, ArgumentOf(call, fate.ParameterOrdinal)).Where(_delegates.ContainsKey).ToArray();
            // On a task member the holder is the innermost completion value, inside the chain of tasks a task(…) result of the
            // member's depth makes (R4).
            IReadOnlyCollection<string> holders = fate.Holder == IrHolderKind.Result && !call.IsConstructor
                ? call.Library!.ResultTypeKey is { } holderType ? [HolderAtCall(caller, call, holderType)] : []
                : Eval(caller, call.Receivers);
            if (holders.Count == 0)
            {
                var handed = regions.Select((values, ordinal) => values.Concat(newInputs[ordinal].Select(input =>
                                              ModelNew(caller, call.OperationId, new IrModelNew(input.TypeKey),
                                                       input.Path(ordinal))))
                                                       .ToTrackedSet(StringComparer.Ordinal)).ToArray();
                // A delegate no object is known for is an unresolved dispatch there, as it would be in that unknown execution (R3).
                if (delegates.Length == 0)
                    UnresolvedHeld(caller, call.OperationId, call.Callee, handed);
                foreach (var region in delegates)
                {
                    CheckCancellation();
                    HandOver(caller, call.OperationId, call.Callee, region, handed);
                }
                return;
            }

            foreach (var holder in holders)
            {
                CheckCancellation();
                if (delegates.Length == 0)
                    Keep(holder, UNRESOLVED_DELEGATE, call.Callee, regions, holderArguments, newInputs);
                foreach (var region in delegates)
                {
                    CheckCancellation();
                    Keep(holder, region, call.Callee, regions, holderArguments, newInputs);
                }
            }
        }

        /// <summary>The holder of the result a known call creates, put where a <c>new</c> result of the member would stand.</summary>
        /// <param name="caller">The instance making the call.</param>
        /// <param name="call">The known call.</param>
        /// <param name="holderType">The holder's type key, the innermost type the member returns.</param>
        private string HolderAtCall(InstanceState caller, SummaryOpaqueCall call, string holderType)
        {
            var created = CreatedObject(caller, call, holderType);
            Add(ResultSlot(caller, call, call.Library!.ReturnTaskDepth, unfollowed: false), [created]);
            return created;
        }

        private void Keep(string holder, string region, string callee, IReadOnlyList<TrackedSet<string>> regions,
                          IReadOnlyList<TrackedSet<int>> holderArguments, IReadOnlyList<TrackedSet<HeldNewInput>> newInputs)
        {
            if (!_held.TryGetValue(holder, out var kept))
            {
                _held.Add(holder, kept = new TrackedMap<string, HeldDelegate>(StringComparer.Ordinal));
                _changes++;
            }

            if (!kept.TryGetValue(region, out var held))
            {
                kept.Add(region, held = new HeldDelegate { Callee = callee });
                _changes++;
            }

            for (var ordinal = 0; ordinal < regions.Count; ordinal++)
            {
                CheckCancellation();
                if (held.Regions.Count <= ordinal)
                {
                    held.Regions.Add(new TrackedSet<string>(StringComparer.Ordinal));
                    held.HolderArguments.Add([]);
                    held.NewInputs.Add([]);
                }

                Add(held.Regions[ordinal], regions[ordinal]);
                foreach (var index in holderArguments[ordinal])
                {
                    CheckCancellation();
                    if (held.HolderArguments[ordinal].Add(index))
                        _changes++;
                }
                foreach (var input in newInputs[ordinal])
                {
                    CheckCancellation();
                    if (held.NewInputs[ordinal].Add(input))
                        _changes++;
                }
            }
        }

        /// <summary>A call without a body of its own on a holder runs every delegate the holder keeps, at the call, in the caller's
        /// execution, each parameter handed what the holder keeps for it and the call's arguments its model names, nothing where the call
        /// has fewer (R4). A delegate no delegate object is known for, or with no body the run has, is an unresolved dispatch there.</summary>
        /// <param name="caller">The caller.</param>
        /// <param name="operationId">The operationId.</param>
        /// <param name="callee">The callee.</param>
        /// <param name="arguments">The arguments.</param>
        /// <param name="holder">The holder.</param>
        private void RunHeld(InstanceState caller, int operationId, string callee, IReadOnlyList<CallArgument> arguments, string holder)
        {
            foreach (var (region, held) in _held[holder].ToArray())
            {
                CheckCancellation();
                var inputs = held.Regions.Select((regions, ordinal) => new CallArgument(ordinal,
                                     regions.Concat(held.HolderArguments[ordinal].SelectMany(index =>
                                                Eval(caller, arguments.FirstOrDefault(argument => argument.ParameterOrdinal == index)?.Values ?? new TrackedSet<AbstractValue>())))
                                            .Concat(held.NewInputs[ordinal].Select(input => ModelNew(caller, operationId,
                                                new IrModelNew(input.TypeKey), input.Path(ordinal))))
                                            .Select(value => (AbstractValue)new RegionValue(value))
                                            .ToTrackedSet()))
                                 .ToArray();
                var invocation = new CallTransfer(operationId, callee, IrCallKind.Delegate,
                                                  region == UNRESOLVED_DELEGATE ? new TrackedSet<AbstractValue>() : new TrackedSet<AbstractValue> { new RegionValue(region) },
                                                  inputs, []);
                if (region == UNRESOLVED_DELEGATE)
                {
                    NoReceiver(caller, invocation);
                    UnresolvedFate(caller, invocation, []);
                    continue;
                }

                var state = _delegates[region];
                var callees = DelegateCallees(caller, operationId, state, () => NoReceiver(caller, invocation));
                if (callees.Count == 0 && !state.IsNestedBody)
                {
                    var declaring = _program.Method(state.Target)?.ContainingTypeKey;
                    UnresolvedFate(caller, invocation, state.CapturedReceivers.Select(receiver => (receiver, declaring)).ToArray());
                }

                foreach (var instance in callees)
                {
                    CheckCancellation();
                    foreach (var input in inputs)
                    {
                        CheckCancellation();
                        Add(Parameter(instance, input.ParameterOrdinal), Eval(caller, input.Values));
                    }
                    Add(instance.Requests, caller.Requests);
                    RunNow(caller, invocation, instance);
                }
            }
        }

        /// <summary>Hands a delegate to its unknown execution at a site, its parameters handed <paramref name="regions"/>. A method group
        /// whose target has no body the run has is an unresolved dispatch there, as a call of it would be (R3).</summary>
        /// <param name="caller">The caller.</param>
        /// <param name="operationId">The operationId.</param>
        /// <param name="callee">The callee.</param>
        /// <param name="region">The region.</param>
        /// <param name="regions">The regions.</param>
        private void HandOver(InstanceState caller, int operationId, string callee, string region, IReadOnlyList<TrackedSet<string>> regions)
        {
            if (!_rebuilding.Handoffs.TryGetValue(region, out var handoff))
                _rebuilding.Handoffs.Add(region, handoff = ([], new TrackedSet<string>(StringComparer.Ordinal)));
            handoff.Sites.Add((caller.Id, operationId));
            var state = _delegates[region];
            var callees = DelegateCallees(caller, operationId, state, () => { });
            foreach (var instance in callees)
            {
                CheckCancellation();
                for (var ordinal = 0; ordinal < regions.Count; ordinal++)
                {
                    CheckCancellation();
                    Add(Parameter(instance, ordinal), regions[ordinal]);
                }
                Add(instance.Requests, caller.Requests);
                handoff.Callees.Add(instance.Id);
            }

            if (callees.Count == 0 && !state.IsNestedBody)
            {
                var declaring = _program.Method(state.Target)?.ContainingTypeKey;
                UnresolvedFate(caller, HeldInvocation(operationId, callee, new TrackedSet<AbstractValue> { new RegionValue(region) }, regions),
                               state.CapturedReceivers.Select(receiver => (receiver, declaring)).ToArray());
            }
        }

        /// <summary>A held delegate no delegate object is known for, run in an unknown execution from a site: an unresolved dispatch there,
        /// which sees what the holder keeps for it (R3, R4).</summary>
        /// <param name="caller">The caller.</param>
        /// <param name="operationId">The operationId.</param>
        /// <param name="callee">The callee.</param>
        /// <param name="regions">The regions.</param>
        private void UnresolvedHeld(InstanceState caller, int operationId, string callee, IReadOnlyList<TrackedSet<string>> regions)
        {
            var invocation = HeldInvocation(operationId, callee, new TrackedSet<AbstractValue>(), regions);
            NoReceiver(caller, invocation);
            UnresolvedFate(caller, invocation, []);
        }

        private static CallTransfer HeldInvocation(int operationId, string callee, IReadOnlySet<AbstractValue> receivers, IReadOnlyList<TrackedSet<string>> regions) =>
            new(operationId, callee, IrCallKind.Delegate, receivers,
                regions.Select((values, ordinal) => new CallArgument(ordinal, values.Select(value => (AbstractValue)new RegionValue(value)).ToTrackedSet())).ToArray(),
                []);

        /// <summary>The holders reachable from what a call without a body is handed, the argument itself or through fields, array and
        /// collection cells and captures, escape there: what they keep runs, in addition, in an unknown execution (R4).</summary>
        /// <param name="caller">The caller.</param>
        /// <param name="operationId">The operationId.</param>
        /// <param name="values">The values.</param>
        private void EscapeHolders(InstanceState caller, int operationId, IEnumerable<AbstractValue> values)
        {
            if (_held.IsEmpty)
                return;
            foreach (var holder in Reached(Eval(caller, values)).Where(_held.ContainsKey).ToArray())
            {
                CheckCancellation();
                Escape(holder, caller, operationId);
            }
        }

        /// <summary>A holder captured, however deep, by a delegate the analysis runs in an unknown execution escapes where that delegate
        /// is handed over (R4); what escapes is handed over in turn.</summary>
        private void EscapeCapturedHolders()
        {
            if (_held.Count == 0)
                return;
            var done = new TrackedSet<string>(StringComparer.Ordinal);
            while (_rebuilding.Handoffs.Keys.Where(done.Add).ToArray() is { Length: > 0 } handed)
            {
                CheckCancellation();
                foreach (var region in handed)
                {
                    CheckCancellation();
                    var sites = _rebuilding.Handoffs[region].Sites.ToArray();
                    foreach (var holder in Reached(Captures(region)).Where(_held.ContainsKey).ToArray())
                    {
                        CheckCancellation();
                        foreach (var (caller, operation) in sites)
                        {
                            CheckCancellation();
                            Escape(holder, _instances[caller], operation);
                        }
                    }
                }
            }
        }

        private void Escape(string holder, InstanceState caller, int operationId)
        {
            foreach (var (region, held) in _held[holder].ToArray())
            {
                CheckCancellation();
                var inputs = held.Regions.Select((regions, ordinal) => regions.Concat(held.NewInputs[ordinal].Select(input =>
                                             ModelNew(caller, operationId, new IrModelNew(input.TypeKey),
                                                      input.Path(ordinal))))
                                                      .ToTrackedSet(StringComparer.Ordinal)).ToArray();
                if (region == UNRESOLVED_DELEGATE)
                    UnresolvedHeld(caller, operationId, held.Callee, inputs);
                else
                    HandOver(caller, operationId, held.Callee, region, inputs);
            }
        }

        /// <summary>The regions reachable from <paramref name="starts"/>, the starts included, through fields, storages and captures.</summary>
        /// <param name="starts">The starts.</param>
        private TrackedSet<string> Reached(IEnumerable<string> starts)
        {
            BuildReachIndex();
            ReadState(REACH_KEY);
            var reached = new TrackedSet<string>(starts, StringComparer.Ordinal);
            var pending = new Stack<string>(reached);
            while (pending.TryPop(out var current))
            {
                CheckCancellation();
                var next = (_reachIndex!.GetValueOrDefault(current) ?? []).Concat(_delegates.ContainsKey(current) ? Captures(current) : []);
                foreach (var target in next)
                {
                    CheckCancellation();
                    if (reached.Add(target))
                        pending.Push(target);
                }
            }

            return reached;
        }

        /// <summary>Records an unresolved dispatch with the receiver objects it sees and hands off the delegates it is given (R1, R3). An
        /// async call whose other alternatives start a body has a task that body's returns do not account for alone.</summary>
        /// <param name="caller">The calling instance.</param>
        /// <param name="call">The unresolved call.</param>
        /// <param name="receivers">The receiver objects it sees, each with the type of the run's own declaring the implementation.</param>
        private void Unresolved(InstanceState caller, CallTransfer call, IReadOnlyCollection<(string Region, string? DeclaringTypeKey)> receivers)
        {
            _rebuilding.UnresolvedDispatches.Add((caller.Id, call.OperationId));
            if (_asyncSpawns.TryGetValue((caller.Id, call.OperationId), out var site))
            {
                foreach (var handle in site.Handles.ToArray())
                    Complete(handle, [], true, new TaskCompleter(caller.Id, call.OperationId, TaskCompleterKind.Unseen));
            }
            if (!_rebuilding.UnresolvedReceivers.TryGetValue((caller.Id, call.OperationId), out var known))
                _rebuilding.UnresolvedReceivers.Add((caller.Id, call.OperationId), known = []);
            known.UnionWith(receivers);
            Handoff(caller, call.OperationId, call.Arguments.SelectMany(argument => argument.Values));
            EscapeHolders(caller, call.OperationId, call.Arguments.SelectMany(argument => argument.Values));
        }

        /// <summary>The instances running a delegate's target for an operation of <paramref name="caller"/>; <paramref name="noReceiver"/>
        /// runs when an instance method has no receiver to run on.</summary>
        /// <param name="operationId">The operationId.</param>
        /// <param name="state">The state.</param>
        /// <param name="caller">The caller.</param>
        /// <param name="noReceiver">The noReceiver.</param>
        private TrackedList<InstanceState> DelegateCallees(InstanceState caller, int operationId, DelegateState state, Action noReceiver)
        {
            var callees = new TrackedList<InstanceState>();
            if (state.IsNestedBody)
            {
                var owner = state.CellOwners.Select(id => _instances.GetValueOrDefault(id)).OfType<InstanceState>().FirstOrDefault();
                if (Instance(state.Target, OwnerContext(state.CellOwners), state.OwnerSubstitution, state.CapturedReceivers, state.CellOwners,
                             owner?.IsReceiverless ?? false) is { } nested)
                {
                    callees.Add(nested);
                }

                return callees;
            }

            if (_program.Method(state.Target) is not { } method)
                return callees;
            if (method.IsStatic)
            {
                if (Instance(method.MethodId, $"{caller.BodyId}#{operationId}", Substitution(method, state.ContainingTypeKey, state.MethodTypeArguments),
                             [], [], false) is { } callee)
                {
                    callees.Add(callee);
                }

                ActivateTypeInitializer(state.ContainingTypeKey ?? method.ContainingTypeKey, caller, null);
                return callees;
            }

            if (state.CapturedReceivers.Count == 0)
                noReceiver();
            var isVirtual = !state.IsNonVirtual &&
                            (method.IsVirtual || method.IsAbstract || method.IsOverride || _program.Type(method.ContainingTypeKey)?.IsInterface == true);
            foreach (var receiver in state.CapturedReceivers.ToArray())
            {
                CheckCancellation();
                if (isVirtual)
                {
                    var dispatched = DispatchCallees(receiver, method.MethodId, state.MethodTypeArguments, state.ContainingTypeKey);
                    if (!dispatched.Dispatched)
                        noReceiver();
                    callees.AddRange(dispatched.Callees);
                    continue;
                }

                if (Instance(method.MethodId, receiver + TypeArgumentText(state.MethodTypeArguments),
                             ReceiverSubstitution(_regions[receiver], method, state.MethodTypeArguments), [receiver], [], false) is { } callee)
                {
                    callees.Add(callee);
                }
            }

            return callees;
        }

        /// <summary>A lambda or local function body instance has the context of the member-body instance owning its cells.</summary>
        /// <param name="cellOwners">The instances owning the body's capture cells.</param>
        private static string OwnerContext(IEnumerable<string> cellOwners) => string.Join(",", cellOwners.Order(StringComparer.Ordinal));

        private void Dispatch(InstanceState caller, CallTransfer call, string methodId, string receiver, IReadOnlyList<string> typeArguments,
                              string? interfaceTypeKey, string reason)
        {
            var (dispatched, callees) = DispatchCallees(receiver, methodId, typeArguments, interfaceTypeKey);
            foreach (var callee in callees)
            {
                CheckCancellation();
                Bind(caller, call, callee, reason);
            }

            // A holder decides the call, as a member of the table does on its objects: its member runs what it keeps (R4).
            if (!dispatched && _held.ContainsKey(receiver))
            {
                RunHeld(caller, call.OperationId, call.Callee, call.Arguments, receiver);
                return;
            }

            // A receiver whose type has no implementation with a body runs one the analysis cannot read: however many other types do,
            // the call is unresolved for this one (R1), unless it is an object the call decides as a member of the table (ADR 0010,
            // amendment of the phase 5b third run).
            if (!dispatched && CollectionObjects.Decision(call.Implementations, ObjectKind(receiver, methodId)) is null)
            {
                NoReceiver(caller, call);
                var declaring = _regions[receiver].TypeKey is { } type ? _program.Implementation(type, methodId, interfaceTypeKey)?.ContainingTypeKey : null;
                Unresolved(caller, call, [(receiver, declaring)]);
            }
        }

        /// <summary>Whether a call through an interface decides every receiver object the heap knows for it as a member of the table: it is
        /// then no unresolved call, and runs nothing it is handed, as the member it is on each of them does not (ADR 0010, amendment of the
        /// phase 5b third run). A call the heap knows no receiver object for stays unresolved.</summary>
        /// <param name="instance">The instance making the call.</param>
        /// <param name="call">The call through an interface.</param>
        private bool DecidesEvery(InstanceState instance, SummaryOpaqueCall call) =>
            (call.Implementations.Count != 0 || !_held.IsEmpty && !call.IsConstructor) && Eval(instance, call.Receivers) is { Count: > 0 } receivers &&
            receivers.All(receiver => !call.IsConstructor && _held.ContainsKey(receiver) ||
                                      call.Implementations.Count != 0 && CollectionObjects.Decision(call.Implementations, ObjectKind(receiver)) is not null);

        /// <summary>What a region is to a call through an interface (<see cref="CollectionObjects"/>).</summary>
        /// <param name="regionId">The region.</param>
        /// <param name="interfaceMethod">The interface member called, if any.</param>
        private string? ObjectKind(string regionId, string? interfaceMethod = null) =>
            CollectionObjects.KindOf(_regions[regionId], _program, _scope.Summaries, interfaceMethod);

        /// <summary>The source implementations a receiver region runs for a call of <paramref name="methodId"/>, and whether any of its
        /// types has one.</summary>
        /// <param name="receiver">The receiver.</param>
        /// <param name="typeArguments">The typeArguments.</param>
        /// <param name="interfaceTypeKey">The interfaceTypeKey.</param>
        /// <param name="methodId">The methodId.</param>
        private (bool Dispatched, TrackedList<InstanceState> Callees) DispatchCallees(string receiver, string methodId, IReadOnlyList<string> typeArguments,
                                                                             string? interfaceTypeKey = null)
        {
            var region = _regions[receiver];
            // A factory or instance registration's region dispatches on the types its factory or instance created, if any.
            IReadOnlyCollection<string> types = _registrations.ContainsKey(receiver)
                ? _regionTypes.GetValueOrDefault(receiver)?.Order(StringComparer.Ordinal).ToArray() ?? []
                : region.TypeKey is null ? [] : [region.TypeKey];
            var dispatched = false;
            var callees = new TrackedList<InstanceState>();
            foreach (var type in types)
            {
                CheckCancellation();
                if (_program.Implementation(type, methodId, interfaceTypeKey) is not { HasSourceBody: true } implementation)
                    continue;
                dispatched = true;
                if (Instance(implementation.MethodId, receiver + TypeArgumentText(typeArguments),
                             Substitution(implementation, _program.ConstructedBase(type, implementation.ContainingTypeKey), typeArguments),
                             [receiver], [], false) is { } callee)
                {
                    callees.Add(callee);
                }
            }

            return (dispatched, callees);
        }

        private void Bind(InstanceState caller, CallTransfer call, InstanceState? callee, string reason)
        {
            if (callee is null)
                return;
            foreach (var argument in call.Arguments)
            {
                CheckCancellation();
                Add(Parameter(callee, argument.ParameterOrdinal), Eval(caller, argument.Values));
                foreach (var reference in argument.References)
                    AddLocations(ParameterLocations(callee, argument.ParameterOrdinal), ReferenceLocations(caller, reference));
            }
            if (_scope.Reachable.Bodies.TryGetValue(callee.BodyId, out var body) && body.IsIterator)
            {
                var region = Region($"iterator|{caller.Id}|{call.OperationId}|{callee.Id}", HeapRegionKind.Allocation,
                                    $"iterator:{call.Target}", null, ContextKey(caller), null);
                Add(CallResult(caller, call.OperationId), [region]);
                _iteratorObjects.TryAdd(region, new CallEdge(caller.Id, call.OperationId, callee.Id, reason));
            }
            else if (AsyncSpawnKind(call, callee) is { } kind)
                AsyncSpawn(caller, call.OperationId, callee, kind);
            else
            {
                // A call awaited at once into an async body gives its task, which completes with what the body returns; any other call
                // gives what the body returns, a non-async body's tasks included.
                if (call.IsAwaitedImmediately && IsAsyncBody(callee.BodyId))
                {
                    var task = TaskRegion(caller, call.OperationId);
                    Add(CallResult(caller, call.OperationId), [task]);
                    Complete(task, callee.Returns, callee.ReturnsUnfollowed, new TaskCompleter(callee.Id, -1, TaskCompleterKind.Returns));
                    UnseenAlternative(caller, call.OperationId, task);
                }
                else
                    Add(CallResult(caller, call.OperationId), callee.Returns);
                if (callee.ReturnsUnfollowed && caller.UnfollowedCallResults.Add(call.OperationId))
                    _changes++;
            }
            foreach (var (ordinal, values) in callee.RefParameters)
            {
                CheckCancellation();
                Add(RefResult(caller, call.OperationId, ordinal), values);
            }
            Add(callee.Requests, caller.Requests);
            AddEdge((caller.Id, call.OperationId, callee.Id, reason));
        }

        /// <summary>An edge into an async body (not an async iterator) whose result the caller does not await at once starts that body
        /// as a spawn: <see cref="IrSpawnKind.AsyncVoid"/> for a <c>void</c> body, <see cref="IrSpawnKind.AsyncCall"/> otherwise, since
        /// an async body can only return <c>Task</c>, <c>ValueTask</c>, their generic forms or a type with an async method builder.</summary>
        /// <param name="call">The call edge.</param>
        /// <param name="callee">The instance the call runs.</param>
        private IrSpawnKind? AsyncSpawnKind(CallTransfer call, InstanceState callee) =>
            !call.IsAwaitedImmediately && _scope.Reachable.Bodies.TryGetValue(callee.BodyId, out var body) && body is { IsAsync: true, IsAsyncIterator: false }
                ? body.ReturnType == "void" ? IrSpawnKind.AsyncVoid : IrSpawnKind.AsyncCall
                : null;

        /// <summary>Marks an async spawn edge; an <see cref="IrSpawnKind.AsyncCall"/>'s result is its site's handle region instead of
        /// what the body returns, which is the task's completion value, not the task. A call one of whose alternatives the heap cannot
        /// resolve may run a body it does not see, so what its task completes with is unknown as well.</summary>
        /// <param name="caller">The calling instance.</param>
        /// <param name="operationId">The call's operation.</param>
        /// <param name="callee">The async body's instance.</param>
        /// <param name="kind">The async spawn's kind.</param>
        private void AsyncSpawn(InstanceState caller, int operationId, InstanceState callee, IrSpawnKind kind)
        {
            var site = SiteOf(_asyncSpawns, caller, operationId, () => new SiteState(kind, operationId));
            if (kind == IrSpawnKind.AsyncCall)
            {
                var handle = TaskRegion(caller, operationId);
                Add(site.Handles, [handle]);
                Add(CallResult(caller, operationId), [handle]);
                Complete(handle, callee.Returns, callee.ReturnsUnfollowed || caller.UnresolvedCallTargets.Contains(operationId),
                         new TaskCompleter(callee.Id, -1, TaskCompleterKind.Returns));
                UnseenAlternative(caller, operationId, handle);
            }

            if (site.Callees.Add((callee.Id, SpawnRole.Work)))
                _changes++;
        }

        /// <summary>Records, for the export, that the task of a call one of whose alternatives the heap cannot resolve may be completed by
        /// a body it does not see (<see cref="TaskCompleterKind.Unseen"/>): no producer the export names says what that body returns.</summary>
        /// <param name="caller">The calling instance.</param>
        /// <param name="operationId">The call's operation.</param>
        /// <param name="task">The call's task region.</param>
        private void UnseenAlternative(InstanceState caller, int operationId, string task)
        {
            if (caller.UnresolvedCallTargets.Contains(operationId))
                Completed(task, new TaskCompleter(caller.Id, operationId, TaskCompleterKind.Unseen));
        }

        private void NoReceiver(InstanceState caller, CallTransfer call) => _rebuilding.NoReceiver.Add((caller.BodyId, call.OperationId));

        /// <summary>Marks a dispatch whose receiver may come from an origin points-to does not follow: the bodies it resolves to are not
        /// every body it may run, so nothing that holds for all of them holds for the call.</summary>
        /// <param name="caller">The calling instance.</param>
        /// <param name="call">The dispatch.</param>
        private void UnresolvedTargets(InstanceState caller, CallTransfer call)
        {
            if (!Unfollowed(caller, call.ReceiverUnknownSources, call.ReceiverSourceCalls, call.ReceiverCompletions))
                return;
            if (caller.UnresolvedCallTargets.Add(call.OperationId))
                _changes++;
            // What a body the heap does not have would return is not among the regions of the result either.
            if (caller.UnfollowedCallResults.Add(call.OperationId))
                _changes++;
        }

        /// <summary>The substitution of a method running on a receiver region: the region's type projected onto the method's declaring
        /// type, plus the call's method type arguments.</summary>
        /// <param name="receiver">The receiver region.</param>
        /// <param name="method">The method.</param>
        /// <param name="typeArguments">The call's method type arguments.</param>
        private IReadOnlyDictionary<string, string> ReceiverSubstitution(HeapRegion receiver, ProgramMethod method, IReadOnlyList<string> typeArguments) =>
            Substitution(method, receiver.TypeKey is null ? null : _program.ConstructedBase(receiver.TypeKey, method.ContainingTypeKey), typeArguments);

        private IReadOnlyDictionary<string, string> Substitution(ProgramMethod method, string? containingTypeKey, IReadOnlyList<string> typeArguments)
        {
            var substitution = new TrackedMap<string, string>(StringComparer.Ordinal);
            if (containingTypeKey is not null && _program.Type(method.ContainingTypeKey) is { TypeParameterKeys.Count: > 0 } declaring)
            {
                var (_, arguments) = _program.Decompose(containingTypeKey);
                foreach (var (parameter, argument) in declaring.TypeParameterKeys.Zip(arguments))
                {
                    CheckCancellation();
                    if (parameter != argument)
                        substitution[parameter] = argument;
                }
            }

            foreach (var (parameter, argument) in method.TypeParameterKeys.Zip(typeArguments))
            {
                CheckCancellation();
                if (parameter != argument)
                    substitution[parameter] = argument;
            }

            return substitution.Count == 0 ? NO_SUBSTITUTION : substitution;
        }

        private static string TypeArgumentText(IReadOnlyList<string> typeArguments) =>
            typeArguments.Count == 0 ? "" : $"<{string.Join(",", typeArguments)}>";

        /// <summary>Activates a closed type's type-initializer construction once, recording what triggered it; a reference from the
        /// type initializer itself is not a trigger.</summary>
        /// <param name="typeKey">The typeKey.</param>
        /// <param name="triggerInstance">The triggerInstance.</param>
        /// <param name="triggerRegion">The triggerRegion.</param>
        private void ActivateTypeInitializer(string typeKey, InstanceState? triggerInstance, string? triggerRegion)
        {
            if (_program.Type(typeKey) is not { } type ||
                _program.MethodsOf(type.TypeKey).FirstOrDefault(method => method is { Kind: ProgramMethodKind.TypeInitializer, HasSourceBody: true }) is not { } initializer ||
                triggerInstance?.BodyId == initializer.MethodId)
            {
                return;
            }

            if (!_typeInitializers.TryGetValue(typeKey, out var construction))
            {
                var (_, arguments) = _program.Decompose(typeKey);
                var substitution = type.TypeParameterKeys.Zip(arguments).Where(pair => pair.First != pair.Second)
                                       .ToTrackedMap(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
                // An open type's one construction stands for every closed one, so its regions are merged.
                var instance = Instance(initializer.MethodId, $"type-initializer:{typeKey}", substitution, [], [], false,
                                        merged: _program.IsOpen(typeKey));
                construction = (instance?.Id ?? "", new TrackedSet<string>(StringComparer.Ordinal), new TrackedSet<string>(StringComparer.Ordinal));
                _typeInitializers.Add(typeKey, construction);
                _changes++;
            }

            if (triggerInstance is not null && construction.Instances.Add(triggerInstance.Id))
                _changes++;
            if (triggerRegion is not null && construction.Regions.Add(triggerRegion))
                _changes++;
        }

        private TrackedSet<string> Eval(InstanceState instance, IEnumerable<AbstractValue> values)
        {
            CheckCancellation();
            var result = new TrackedSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                CheckCancellation();
                result.UnionWith(Eval(instance, value));
            }
            return result;
        }

        private TrackedSet<string> Eval(InstanceState instance, AbstractValue value) => value switch
        {
            ThisValue => new TrackedSet<string>(instance.Receivers, StringComparer.Ordinal),
            ParameterValue parameter => new TrackedSet<string>(Parameter(instance, parameter.Ordinal), StringComparer.Ordinal),
            StaticFieldValue field => Load(StaticRegion(instance, field.Field), FieldSlot.Key(field.Field)),
            AllocationValue allocation => [AllocationRegion(instance, allocation.Site)],
            CallResultValue result => new TrackedSet<string>(CallResult(instance, result.OperationId), StringComparer.Ordinal),
            LibraryKeeperValue keeper => _keeperCalls.TryGetValue((instance.Id, keeper.OperationId), out var keepingCalls)
                ? keepingCalls.Values.Where(call => call.Library!.Keeps.ContainsKey(keeper.KeeperOrdinal) || call.Library.KeeperTypeKeys.ContainsKey(keeper.KeeperOrdinal))
                    .SelectMany(call => KeeperTargets(instance, call, keeper.KeeperOrdinal, keeper.Read)).ToTrackedSet(StringComparer.Ordinal) : [],
            LibraryKeepingValue keeping => new TrackedSet<string>(_libraryKeeping.GetValueOrDefault((instance.Id, keeping.OperationId, keeping.KeeperOrdinal)) ?? [], StringComparer.Ordinal),
            LibraryStoredValue stored => new TrackedSet<string>(_libraryStored.GetValueOrDefault((instance.Id, stored.OperationId, stored.TargetOrdinal)) ?? [], StringComparer.Ordinal),
            RefResultValue result => new TrackedSet<string>(RefResult(instance, result.OperationId, result.Ordinal), StringComparer.Ordinal),
            ReferenceLocationValue location => ReferenceLocations(instance, location.Target)
                .SelectMany(target => location.Read ? Load(target.Region, target.Slot) : [target.Region]).ToTrackedSet(StringComparer.Ordinal),
            DelegateCreationValue created => [DelegateRegion(instance, created)],
            CapturedValue captured => instance.CellOwners.SelectMany(owner => Cell(owner, captured.SymbolKey)).ToTrackedSet(StringComparer.Ordinal),
            AwaitResultValue awaited => Completion(instance.Summary.Joins.Where(join => join.OperationId == awaited.OperationId)
                                                           .SelectMany(join => join.Handles)
                                                           .SelectMany(handle => Eval(instance, handle.Values))),
            PathValue path => EvalPath(instance, path),
            RegionValue region => [region.RegionId],
            _ => new TrackedSet<string>(StringComparer.Ordinal)
        };

        private TrackedSet<string> EvalPath(InstanceState instance, PathValue path)
        {
            var current = Eval(instance, path.Base);
            foreach (var segment in path.Segments)
            {
                CheckCancellation();
                var next = new TrackedSet<string>(StringComparer.Ordinal);
                foreach (var region in current)
                {
                    CheckCancellation();
                    next.UnionWith(segment == PathValue.WILDCARD ? Closure(region) : Load(region, segment));
                }

                current = next;
            }

            return current;
        }

        /// <summary>Every region reachable from a region through any fields.</summary>
        /// <param name="region">The region.</param>
        private TrackedSet<string> Closure(string region)
        {
            CheckCancellation();
            var seen = new TrackedSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>([region]);
            while (pending.TryPop(out var current))
            {
                CheckCancellation();
                foreach (var field in _fieldsByRegion.GetValueOrDefault(current)?.ToArray() ?? [])
                {
                    CheckCancellation();
                    foreach (var target in _fields[(current, field)])
                    {
                        CheckCancellation();
                        if (seen.Add(target))
                            pending.Push(target);
                    }
                }
            }

            return seen;
        }

        /// <summary>A field slot of a region, or, for a bare field name, every declaring type's slot of that name.</summary>
        /// <param name="regionId">The regionId.</param>
        /// <param name="field">The field.</param>
        private TrackedSet<string> LoadAny(string regionId, string field)
        {
            if (field.Contains('.', StringComparison.Ordinal) || PathValue.IsStorage(field) || field is PathValue.NODE_LIST or PathValue.VIEWED)
                return Load(regionId, field);

            var result = Load(regionId, field);
            foreach (var slot in (_fieldsByRegion.GetValueOrDefault(regionId) ?? []).Where(slot => FieldSlot.Name(slot) == field))
            {
                CheckCancellation();
                result.UnionWith(Load(regionId, slot));
            }
            return result;
        }

        /// <summary>A field of a region, joined with the same field of the open regions of its group, or of every region of the group
        /// when the region itself is open.</summary>
        /// <param name="regionId">The regionId.</param>
        /// <param name="field">The field.</param>
        private TrackedSet<string> Load(string regionId, string field)
        {
            var result = new TrackedSet<string>(StringComparer.Ordinal);
            if (!_regions.TryGetValue(regionId, out var region))
                return result;
            foreach (var member in _groups[region.Group])
            {
                CheckCancellation();
                if (member == regionId || region.IsOpen || _regions[member].IsOpen)
                    result.UnionWith(_fields.GetValueOrDefault((member, field)) ?? []);
            }

            return result;
        }

        private string StaticTypeKey(InstanceState instance, IrFieldRef field) =>
            ProgramIndex.Substitute(DiIndex.TypeKey(field.Assembly, field.ContainingTypeId), instance.Substitution);

        private string StaticRegion(InstanceState instance, IrFieldRef field)
        {
            var typeKey = StaticTypeKey(instance, field);
            return Region($"static:{typeKey}", HeapRegionKind.Static, $"static:{DisplayType(typeKey)}", typeKey, "",
                          $"static|{_program.Decompose(typeKey).DefinitionKey}", merged: instance.IsMerged && _program.IsOpen(typeKey));
        }

        private string StaticRegion(string typeKey) =>
            Region($"static:{typeKey}", HeapRegionKind.Static, $"static:{DisplayType(typeKey)}", typeKey, "", $"static|{typeKey}");

        private string AllocationRegion(InstanceState instance, CreationSite site)
        {
            var typeKey = ProgramIndex.Substitute(site.TypeKey, instance.Substitution);
            if (_registeredAllocations.TryGetValue((site.BodyId, site.OperationId, ContextKey(instance)), out var registered) ||
                _factoryInstances.TryGetValue(instance.Id, out registered))
            {
                // The types of a registered region decide the solve's dispatch alone.
                if (Querying)
                    return registered;
                if (!_regionTypes.TryGetValue(registered, out var types))
                    _regionTypes.Add(registered, types = new TrackedSet<string>(StringComparer.Ordinal));
                Add(types, [typeKey]);
                return registered;
            }

            var owner = _scope.Reachable.Bodies.TryGetValue(site.BodyId, out var body) ? body.OwnerSymbol : site.BodyId;
            var ordinal = site.SiteOrdinal > 1 ? $"#{site.SiteOrdinal}" : "";
            return Region($"alloc|{site.BodyId}#{site.OperationId}|{typeKey}|{ContextKey(instance)}", HeapRegionKind.Allocation,
                          $"alloc:{owner}#{DisplayType(typeKey)}{ordinal}", typeKey, instance.Context, $"alloc|{site.BodyId}#{site.OperationId}",
                          merged: instance.IsMerged, site: site,
                          exactType: _scope.Reachable.Bodies.TryGetValue(site.BodyId, out var lowered) &&
                                     lowered.Blocks.SelectMany(block => block.Operations)
                                            .Any(operation => operation is IrAllocateOperation allocation && allocation.Id == site.OperationId));
        }

        private string DelegateRegion(InstanceState instance, DelegateCreationValue created)
        {
            var site = created.Site;
            var identity = $"delegate|{site.BodyId}#{site.OperationId}|{ContextKey(instance)}";
            if (_delegates.ContainsKey(identity))
                return identity;

            var transfer = instance.Summary.Delegates.FirstOrDefault(candidate => candidate.OperationId == site.OperationId);
            var method = _program.Method(created.Target);
            var owner = _scope.Reachable.Bodies.TryGetValue(site.BodyId, out var body) ? body.OwnerSymbol : site.BodyId;
            var ordinal = site.SiteOrdinal > 1 ? $"#{site.SiteOrdinal}" : "";
            Region(identity, HeapRegionKind.Delegate, $"delegate:{owner}#{method?.DisplaySymbol ?? created.Target}{ordinal}",
                   SubstituteDisplay(site.TypeKey, instance.Substitution),
                   instance.Context, $"delegate|{site.BodyId}#{site.OperationId}", merged: instance.IsMerged, site: site);
            // A delegate a query names keeps no state: the solve recorded nothing it captures.
            if (Querying)
                return identity;
            _delegates[identity] = new DelegateState(
                created.Target, method is null,
                transfer?.TargetContainingTypeKey is { } containing ? ProgramIndex.Substitute(containing, instance.Substitution) : null,
                (transfer?.TargetMethodTypeArgumentKeys ?? []).Select(key => ProgramIndex.Substitute(key, instance.Substitution)).ToArray(),
                instance.Substitution, transfer?.IsNonVirtual == true);
            return identity;
        }

        /// <summary>A delegate creation names its type as the IR value's display type, not as a type key, so its substitution is
        /// applied by display name: <c>Action&lt;T&gt;</c> created in an instance of <c>T = Order</c> is <c>Action&lt;Order&gt;</c>.</summary>
        /// <param name="type">The type.</param>
        /// <param name="substitution">The substitution.</param>
        private string SubstituteDisplay(string type, IReadOnlyDictionary<string, string> substitution)
        {
            if (substitution.Count == 0)
                return type;
            var byDisplay = new TrackedMap<string, string>(StringComparer.Ordinal);
            foreach (var (parameter, argument) in substitution)
            {
                CheckCancellation();
                byDisplay.TryAdd(DisplayType(parameter), DisplayType(argument));
            }
            return ProgramIndex.Substitute(type, byDisplay);
        }

        /// <summary>An instance's context with its substitution: one body reached from one call site with two type arguments runs in
        /// two contexts, so it creates two objects, even when the created type itself is not generic.</summary>
        /// <param name="instance">The instance.</param>
        private static string ContextKey(InstanceState instance) =>
            instance.Substitution.Count == 0
                ? instance.Context
                : $"{instance.Context}|{string.Join(",", instance.Substitution.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                                                             .Select(pair => $"{pair.Key}={pair.Value}"))}";

        private string Region(string identity, HeapRegionKind kind, string display, string? typeKey, string context, string? group,
                              bool merged = false, bool mayOverlapItself = false, CreationSite? site = null, bool exactType = false,
                              string? modelCreationKey = null)
        {
            if (_regions.ContainsKey(identity))
                return identity;
            group ??= identity;
            var region = new HeapRegion(identity, kind, display, typeKey, context, group, typeKey is not null && _program.IsOpen(typeKey), merged,
                                        mayOverlapItself, site?.BodyId, site?.OperationId) { HasExactType = exactType, ModelCreationKey = modelCreationKey };
            if (Querying)
            {
                // A query names a region the solve did not make and adds it nowhere, so every stage sees the heap the solve made.
                _named.TryAdd(identity, region);
                return identity;
            }
            _regions.Add(identity, region);
            if (!_groups.TryGetValue(group, out var members))
                _groups.Add(group, members = []);
            members.Add(identity);
            _changes++;
            return identity;
        }

        private TrackedSet<string> CapturedKeys(string memberId)
        {
            if (_capturedKeysOfMember.TryGetValue(memberId, out var keys))
                return keys;
            keys = new TrackedSet<string>(StringComparer.Ordinal);
            var nested = _program.Method(memberId)?.NestedBodyIds ?? [];
            foreach (var bodyId in nested)
            {
                CheckCancellation();
                if (_scope.Reachable.Bodies.TryGetValue(bodyId, out var body))
                {
                    keys.UnionWith(body.Blocks.SelectMany(block => block.Operations).OfType<IrCaptureOperation>()
                                       .Select(capture => capture.SymbolKey).OfType<string>());
                }
            }

            _capturedKeysOfMember.Add(memberId, keys);
            return keys;
        }

        /// <summary>Counts a changing round of an instance; a call-graph cycle that keeps changing past the budget has its bodies'
        /// contexts merged, and propagation continues.</summary>
        /// <param name="instance">The instance whose facts changed in this round.</param>
        private void CountRound(InstanceState instance)
        {
            var rounds = _changedRounds.GetValueOrDefault(instance.Id) + 1;
            _changedRounds[instance.Id] = rounds;
            if (rounds <= _limits.MaxSccIterations || _sccHandled.Contains(instance.Id))
                return;

            var component = Component(instance.Id);
            var cyclic = component.Count > 1 || (_edgesByCaller.GetValueOrDefault(instance.Id) ?? []).Any(edge => edge.Callee == instance.Id);
            _sccHandled.UnionWith(component);
            if (!cyclic)
                return;

            _mergedBodies.UnionWith(component.Select(id => _instances[id].BodyId));
            _counters[HeapCounters.SCC_BUDGET_EXCEEDED] = _counters.GetValueOrDefault(HeapCounters.SCC_BUDGET_EXCEEDED) + 1;
        }

        /// <summary>The strongly connected component of the instance call graph that contains <paramref name="start"/>: the instances
        /// it reaches that also reach it.</summary>
        /// <param name="start">The instance whose component is returned.</param>
        private TrackedSet<string> Component(string start)
        {
            var forward = Reach(start, id => (_edgesByCaller.GetValueOrDefault(id) ?? []).Select(edge => edge.Callee));
            var backward = Reach(start, id => (_edgesByCallee.GetValueOrDefault(id) ?? []).Select(edge => edge.Caller));
            forward.IntersectWith(backward);
            forward.Add(start);
            return forward;
        }

        private TrackedSet<string> Reach(string start, Func<string, IEnumerable<string>> neighbors)
        {
            var seen = new TrackedSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>([start]);
            while (pending.TryPop(out var current))
            {
                CheckCancellation();
                foreach (var next in neighbors(current))
                {
                    CheckCancellation();
                    if (seen.Add(next))
                        pending.Push(next);
                }
            }

            return seen;
        }

        private void AddEdge((string Caller, int Operation, string Callee, string Reason) edge)
        {
            if (!_edges.Add(edge))
                return;
            if (!_edgesByCallee.TryGetValue(edge.Callee, out var incoming))
                _edgesByCallee.Add(edge.Callee, incoming = []);
            incoming.Add(edge);
            if (!_edgesByCaller.TryGetValue(edge.Caller, out var outgoing))
                _edgesByCaller.Add(edge.Caller, outgoing = []);
            outgoing.Add(edge);
            if (!_edgesByCall.TryGetValue((edge.Caller, edge.Operation), out var call))
                _edgesByCall.Add((edge.Caller, edge.Operation), call = []);
            call.Add(edge);
            _changes++;
        }

        private TrackedSet<string> Field(string region, string field)
        {
            if (!_fields.TryGetValue((region, field), out var values))
            {
                _fields.Add((region, field), values = new TrackedSet<string>(StringComparer.Ordinal));
                if (!_fieldsByRegion.TryGetValue(region, out var fields))
                    _fieldsByRegion.Add(region, fields = []);
                fields.Add(field);
            }
            return values;
        }

        private TrackedSet<string> Cell(string owner, string key) => Get(_cells, (owner, key));

        private TrackedSet<string> Parameter(InstanceState instance, int ordinal) => Get(instance.Parameters, ordinal);

        private TrackedSet<string> CallResult(InstanceState instance, int operation) => Get(instance.CallResults, operation);

        /// <summary>Whether a value may come from an origin points-to does not follow: a parameter, a captured variable, an opaque call or
        /// an operation the summary does not model. A null or a field read before its first write is no object, and a source call is
        /// followed to its callee, so it counts only when that callee's own result is unfollowed; a task's completion counts only when one
        /// of the tasks its await or <c>Unwrap()</c> reads may complete with such an object.</summary>
        /// <param name="instance">The instance expressing the value.</param>
        /// <param name="sources">The value's unknown sources.</param>
        /// <param name="calls">The value's source calls.</param>
        /// <param name="completions">The awaits and <c>Unwrap()</c> calls whose completion the value may be.</param>
        private static bool Unfollowed(InstanceState instance, IReadOnlySet<UnknownSource> sources, IReadOnlySet<int> calls, IReadOnlySet<int> completions) =>
            sources.Any(source => source is not (UnknownSource.Null or UnknownSource.FieldBeforeWrite or UnknownSource.SourceCall)) ||
            calls.Any(instance.UnfollowedCallResults.Contains) || completions.Any(instance.UnfollowedCompletions.Contains);

        private TrackedSet<string> RefResult(InstanceState instance, int operation, int ordinal) => Get(instance.RefResults, (operation, ordinal));

        private TrackedSet<string> RefParameter(InstanceState instance, int ordinal) => Get(instance.RefParameters, ordinal);

        private TrackedSet<string> Get<TKey>(TrackedMap<TKey, TrackedSet<string>> map, TKey key) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var set))
            {
                set = new TrackedSet<string>(StringComparer.Ordinal);
                // A query reads an empty set where the solve left none, and leaves the map as it was.
                if (!Querying)
                    map.Add(key, set);
            }
            return set;
        }

        private TrackedSet<(string Region, string Slot)> ParameterLocations(InstanceState instance, int ordinal)
        {
            if (!instance.ParameterLocations.TryGetValue(ordinal, out var locations))
            {
                locations = [];
                if (!Querying)
                    instance.ParameterLocations.Add(ordinal, locations);
            }
            return locations;
        }

        private void AddLocations(TrackedSet<(string Region, string Slot)> target, IEnumerable<(string Region, string Slot)> locations)
        {
            foreach (var location in locations.ToArray())
            {
                CheckCancellation();
                if (target.Add(location))
                    _changes++;
            }
        }

        private void Add(TrackedSet<string> target, IEnumerable<string> values)
        {
            foreach (var value in values.ToArray())
            {
                CheckCancellation();
                if (target.Add(value))
                    _changes++;
            }
        }
    }
}
