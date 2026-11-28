using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace ConcurrencyHunter;

/// <summary>Goes on with work that recurses by the depth of its input on a thread of its own once the current thread's stack runs
/// short, as <c>System.Linq.Expressions</c> does when it compiles a deep tree: the same call, made on a fresh stack, the caller
/// waiting for it. The work is the same computation in the same order, so its answer is the one the recursion gives on any stack; only
/// memory bounds its depth (issue 561).</summary>
internal static class FreshStack
{
    /// <summary>The stack of a thread the work goes on on, in bytes. A thread reserves it and commits only the pages the work touches,
    /// so a larger stack costs address space and no memory; it holds about two thousand levels of a lift of locks, so a chain of
    /// twenty thousand calls goes on on about ten threads, each started once.</summary>
    private const int STACK_SIZE = 16 * 1024 * 1024;

    /// <summary>Whether the current thread's stack is too short to go on safely (<see cref="RuntimeHelpers.TryEnsureSufficientExecutionStack"/>).</summary>
    internal static bool IsShort => !RuntimeHelpers.TryEnsureSufficientExecutionStack();

    /// <summary>Makes a call on a new thread with a stack of <see cref="STACK_SIZE"/> and waits for it. The thread runs in the caller's
    /// execution context, so the work's cancellation (<see cref="WorkCancellation"/>) goes with it; what the call throws, a
    /// cancellation included, is thrown here with its own stack trace. The thread is not taken from the pool: the caller blocks while it
    /// runs, and a pool thread blocked so would starve the pool.</summary>
    /// <param name="work">The call.</param>
    /// <typeparam name="T">What the call gives back.</typeparam>
    internal static T Run<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
        }, STACK_SIZE) { Name = "ch-fresh-stack", IsBackground = true };
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
