using ConcurrencyHunter.Execution;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>What an execution of a driver run is to the driver.</summary>
public enum DriverExecutionRole
{
    /// <summary>The root execution of <c>V_Call</c>, <c>V_Enum</c> or a trigger: own(X).</summary>
    Own,

    /// <summary>An execution of the tree of <c>V_Call</c>, <c>V_Enum</c> or a trigger other than its root.</summary>
    Child,

    /// <summary>An execution of the tree of <c>V_Setup</c>.</summary>
    Setup,

    /// <summary>The unknown enumeration of the result the driver stored, or an execution of its tree: the escape artefact.</summary>
    Artefact,

    /// <summary>Any other execution: a lazy construction, a type initializer, an unknown execution, anything.</summary>
    Elsewhere
}

/// <summary>An execution's role to the driver, with the action whose tree it is in.</summary>
/// <param name="Role">The role.</param>
/// <param name="Action">The action, for <see cref="DriverExecutionRole.Own"/>, <see cref="DriverExecutionRole.Child"/> and
/// <see cref="DriverExecutionRole.Setup"/>; else <c>null</c>.</param>
public sealed record DriverExecution(DriverExecutionRole Role, string? Action);

/// <summary>The driver's executions (SPEC TD-034b, G-0), the one owner of the question: own(X) is action X's root execution; tree(X)
/// is own(X) and every execution whose tree root is X; the artefact is an unknown enumeration whose subject is reachable from
/// <c>Keep.R</c>, with every execution whose tree root it is; setup is tree(<c>V_Setup</c>); elsewhere is any other execution.</summary>
public sealed class DriverExecutions
{
    private readonly Dictionary<string, string> _treeRoots;
    private readonly Dictionary<string, string> _actionsByRoot = new(StringComparer.Ordinal);
    private readonly HashSet<string> _artefacts = new(StringComparer.Ordinal);

    /// <summary>The executions of one driver run.</summary>
    /// <param name="driver">The driver.</param>
    /// <param name="executions">The run's executions.</param>
    /// <param name="reachableFromResult">The objects reachable from <c>Keep.R</c>.</param>
    public DriverExecutions(Driver driver, ExecutionAnalysis executions, IReadOnlySet<string> reachableFromResult)
    {
        _treeRoots = executions.Executions.ToDictionary(execution => execution.Id, execution => execution.TreeRootId, StringComparer.Ordinal);
        foreach (var action in driver.Actions.Concat(driver.Triggers.Select(trigger => trigger.Action)))
            _actionsByRoot[DriverRootProvider.RootId(action)] = action;
        foreach (var execution in executions.Executions.Where(execution => execution.Kind == ExecutionKind.UnknownEnumeration &&
                                                                           execution.Subject is { } subject && reachableFromResult.Contains(subject)))
        {
            _artefacts.Add(execution.Id);
        }
    }

    /// <summary>The id of an action's root execution, own(X).</summary>
    /// <param name="action">The action.</param>
    public static string Own(string action) => $"root:{DriverRootProvider.RootId(action)}";

    /// <summary>Whether an execution is own(X).</summary>
    /// <param name="executionId">The execution.</param>
    /// <param name="action">The action.</param>
    public static bool IsOwn(string executionId, string action) => executionId == Own(action);

    /// <summary>Whether an execution is own(Y) of a driver action Y other than <paramref name="action"/> and setup. Each action builds
    /// its own receiver and arguments, so a probe of X reaches own(Y) only through a static, which the heap keeps for good, or through a
    /// library method instance the engine shares between the two actions' calls.</summary>
    /// <param name="executionId">The execution.</param>
    /// <param name="action">The action that hands the probe over.</param>
    public bool IsOwnOfAnotherAction(string executionId, string action) => IsOwnOfAnAction(executionId) && !IsOwn(executionId, action);

    /// <summary>Whether an execution is own(Y) of a driver action Y other than setup — <c>V_Call</c>, <c>V_Enum</c> or a trigger.</summary>
    /// <param name="executionId">The execution.</param>
    public bool IsOwnOfAnAction(string executionId) =>
        _actionsByRoot.Values.Any(action => action != DriverSynthesizer.SETUP && IsOwn(executionId, action));

    /// <summary>Whether an execution is in tree(X).</summary>
    /// <param name="executionId">The execution.</param>
    /// <param name="action">The action.</param>
    public bool InTree(string executionId, string action) => _actionsByRoot.GetValueOrDefault(TreeRootOf(executionId)) == action;

    /// <summary>Whether an execution is in setup's tree.</summary>
    /// <param name="executionId">The execution.</param>
    public bool InSetup(string executionId) => InTree(executionId, DriverSynthesizer.SETUP);

    /// <summary>Whether an execution is the escape artefact or runs in its tree.</summary>
    /// <param name="executionId">The execution.</param>
    public bool IsArtefact(string executionId) => _artefacts.Contains(executionId) || _artefacts.Contains(TreeRootOf(executionId));

    /// <summary>An execution's role.</summary>
    /// <param name="executionId">The execution.</param>
    public DriverExecution Of(string executionId)
    {
        if (IsArtefact(executionId))
            return new DriverExecution(DriverExecutionRole.Artefact, null);
        if (!_actionsByRoot.TryGetValue(TreeRootOf(executionId), out var action))
            return new DriverExecution(DriverExecutionRole.Elsewhere, null);
        if (action == DriverSynthesizer.SETUP)
            return new DriverExecution(DriverExecutionRole.Setup, action);
        return new DriverExecution(IsOwn(executionId, action) ? DriverExecutionRole.Own : DriverExecutionRole.Child, action);
    }

    private string TreeRootOf(string executionId) => _treeRoots.GetValueOrDefault(executionId) ?? executionId;
}
