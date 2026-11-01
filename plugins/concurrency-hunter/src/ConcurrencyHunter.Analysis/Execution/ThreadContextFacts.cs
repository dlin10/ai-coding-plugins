using System.Text.RegularExpressions;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Execution;

/// <summary>Where a task runs its work, as the code the analysis sees says (ADR 0030): on the default scheduler, on the scheduler of
/// the thread that starts it, or on a scheduler the program names.</summary>
internal enum TaskScheduling
{
    Default,
    Thread,
    Program
}

/// <summary>
/// What one process scope's visible code says against the thread-context assumption of ADR 0030: whether a reachable call installs
/// another thread context — sets the synchronization context, runs a UI message loop, interrupts a thread — and whether a reachable
/// construction of a <c>TaskFactory</c> or <c>TaskFactory&lt;T&gt;</c> names a scheduler other than <c>TaskScheduler.Default</c>. A call is
/// reachable when it stands in a body the reachable set reached, on any branch of it: a body the analysis does not reach gives nothing.
/// </summary>
internal sealed partial class ThreadContextFacts
{
    /// <summary>The members whose call installs another thread context, every overload of each, as the lowering names a call's
    /// method: the containing type, a dot, the member's name and its parameter list.</summary>
    private static readonly string[] Installers =
    [
        "System.Threading.SynchronizationContext.SetSynchronizationContext(",
        "System.Windows.Forms.Application.Run(",
        "System.Windows.Threading.Dispatcher.Run(",
        "System.Windows.Application.Run(",
        "System.Threading.Thread.Interrupt("
    ];

    private const string DEFAULT_SCHEDULER = "System.Threading.Tasks.TaskScheduler.get_Default()";

    private ThreadContextFacts(bool installsContext, bool constructsSchedulerFactory)
    {
        InstallsContext = installsContext;
        ConstructsSchedulerFactory = constructsSchedulerFactory;
    }

    /// <summary>Whether a reachable call installs another thread context.</summary>
    internal bool InstallsContext { get; }

    /// <summary>Whether a reachable construction of a task factory names a scheduler other than <c>TaskScheduler.Default</c>.</summary>
    internal bool ConstructsSchedulerFactory { get; }

    /// <summary>Reads the facts of a process scope once, over every operation of every body its reachable set reached.</summary>
    /// <param name="scope">The process scope.</param>
    internal static ThreadContextFacts Of(ScopeProgram scope)
    {
        var installs = false;
        var factory = false;
        foreach (var bodyId in scope.Reachable.ReachedBodies.Keys)
        {
            WorkCancellation.Check();
            if (!scope.Reachable.Bodies.TryGetValue(bodyId, out var body))
                continue;
            var operations = body.Blocks.SelectMany(block => block.Operations).ToArray();
            foreach (var call in operations.OfType<IrCallOperation>())
            {
                WorkCancellation.Check();
                installs |= Installers.Any(installer => call.Method.StartsWith(installer, StringComparison.Ordinal));
                factory |= !factory && NamesSchedulerFactory(call, operations);
            }
        }

        return new ThreadContextFacts(installs, factory);
    }

    /// <summary>Where a spawned task runs its work (D-0023). A <c>StartNew</c> or <c>ContinueWith</c> given a scheduler argument
    /// runs where that argument says: a read of <c>TaskScheduler.Default</c> on the default scheduler, any other on the program's.
    /// Without one, a <c>StartNew</c> on a factory other than <c>Task.Factory</c> runs on the program's scheduler where the scope
    /// constructs a factory on one, and every other task without one takes the scheduler of the thread that starts it. A
    /// <c>Task.Run</c> runs on the default scheduler, as a read of <c>TaskScheduler.Default</c> does, and so, for this question, does
    /// work that is no task.</summary>
    /// <param name="site">The spawn.</param>
    internal TaskScheduling Scheduling(SpawnSite site) =>
        site.Kind is not (IrSpawnKind.StartNew or IrSpawnKind.ContinueWith) ? TaskScheduling.Default
        : site.SchedulerArgument switch
        {
            IrSchedulerArgument.Other => TaskScheduling.Program,
            IrSchedulerArgument.Default => TaskScheduling.Default,
            _ => site.Kind == IrSpawnKind.StartNew && site.OnSeparateFactory && ConstructsSchedulerFactory ? TaskScheduling.Program : TaskScheduling.Thread
        };

    /// <summary>Whether a call constructs a task factory with a scheduler argument that is not a read of <c>TaskScheduler.Default</c>,
    /// directly or through assignments and conversions in the body: a conversion of a scheduler leaves it the scheduler it was, as the
    /// lowering reads a <c>StartNew</c> or <c>ContinueWith</c> argument without its conversions.</summary>
    /// <param name="call">The call.</param>
    /// <param name="operations">The operations of the body making it.</param>
    private static bool NamesSchedulerFactory(IrCallOperation call, IReadOnlyList<IrOperation> operations)
    {
        if (FactoryConstructor().Match(call.Method) is not { Success: true } match)
            return false;
        var ordinal = Array.IndexOf(match.Groups["parameters"].Value.Split(", "), "TaskScheduler");
        if (ordinal < 0 || call.ArgumentAt(ordinal) is not int value)
            return false;
        var seen = new HashSet<int>();
        while (seen.Add(value))
        {
            WorkCancellation.Check();
            var definer = operations.FirstOrDefault(operation => operation.DefinedValues.Contains(value));
            if (definer is IrAssignOperation assign)
                value = assign.SourceValue;
            else if (definer is IrConvertOperation convert)
                value = convert.OperandValue;
            else
                return definer is not IrCallOperation { Method: DEFAULT_SCHEDULER };
        }

        return true;
    }

    [GeneratedRegex(@"^System\.Threading\.Tasks\.TaskFactory(<[^()]*>)?\.\.ctor\((?<parameters>.*)\)$")]
    private static partial Regex FactoryConstructor();
}
