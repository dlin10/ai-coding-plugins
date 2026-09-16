using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class MustHeldLockTests
{
    private const string G_ID = "scope:Fixture|alloc|body:Fixture:M:Gates.#cctor#0|System.Private.CoreLib:object|type-initializer:Fixture:Gates";

    private const string Shared = """
        public static class Gates
        {
            public static readonly object G = new();
            public static readonly object H = new();
            public static object Mutable = new();
            public static int Counter;
        }
        public static class State { public static int Value; public static int Other; }

        """;

    [Fact]
    public void Write_inside_a_static_readonly_lock_holds_the_single_object()
    {
        var write = Action("lock (Gates.G) { State.Value = 1; }").Single("Value", AccessOperation.Write);

        Assert.Equal([G_ID], write.HeldProtectionIds);
        Assert.Equal(["alloc:Gates..cctor()#object"], write.HeldProtection);
        Assert.Contains(write.CodeFlow, step => step.Kind == "acquire");
    }

    [Fact]
    public void Write_after_a_nested_reacquisition_is_still_held()
    {
        var write = Action("lock (Gates.G) { lock (Gates.G) { } State.Value = 1; }").Single("Value", AccessOperation.Write);

        Assert.Equal([G_ID], write.HeldProtectionIds);
    }

    [Fact]
    public void Release_of_an_unresolved_object_releases_every_monitor()
    {
        var write = Action("""
            System.Threading.Monitor.Enter(Gates.G);
            var gate = condition ? Gates.G : Gates.H;
            System.Threading.Monitor.Exit(gate);
            State.Value = 1;
            """).Single("Value", AccessOperation.Write);

        Assert.Empty(write.HeldProtection);
        Assert.Empty(write.HeldProtectionIds);
    }

    [Fact]
    public void Write_after_a_lock_statement_is_not_held()
    {
        var run = Action("lock (Gates.G) { State.Other = 1; } State.Value = 1;");

        Assert.Equal([G_ID], run.Single("Other", AccessOperation.Write).HeldProtectionIds);
        Assert.Empty(run.Single("Value", AccessOperation.Write).HeldProtectionIds);
    }

    [Fact]
    public void Write_after_an_explicit_try_finally_exit_is_not_held()
    {
        var run = Action("""
            System.Threading.Monitor.Enter(Gates.G);
            try { State.Other = 1; }
            finally { System.Threading.Monitor.Exit(Gates.G); }
            State.Value = 1;
            """);

        Assert.Equal([G_ID], run.Single("Other", AccessOperation.Write).HeldProtectionIds);
        Assert.Empty(run.Single("Value", AccessOperation.Write).HeldProtectionIds);
    }

    [Fact]
    public void Two_enters_and_one_exit_hold_until_the_second_exit()
    {
        var run = Action("""
            System.Threading.Monitor.Enter(Gates.G);
            System.Threading.Monitor.Enter(Gates.G);
            System.Threading.Monitor.Exit(Gates.G);
            State.Other = 1;
            System.Threading.Monitor.Exit(Gates.G);
            State.Value = 1;
            """);

        Assert.Equal([G_ID], run.Single("Other", AccessOperation.Write).HeldProtectionIds);
        Assert.Empty(run.Single("Value", AccessOperation.Write).HeldProtectionIds);
    }

    [Fact]
    public void Lock_taken_on_one_branch_is_not_held_after_the_join()
    {
        var write = Action("""
            if (condition) System.Threading.Monitor.Enter(Gates.G);
            State.Value = 1;
            """).Single("Value", AccessOperation.Write);

        Assert.Empty(write.HeldProtectionIds);
    }

    [Fact]
    public void Join_of_different_depths_keeps_the_smaller_depth()
    {
        var write = Action("""
            System.Threading.Monitor.Enter(Gates.G);
            if (condition) System.Threading.Monitor.Enter(Gates.G);
            System.Threading.Monitor.Exit(Gates.G);
            State.Value = 1;
            """).Single("Value", AccessOperation.Write);

        Assert.Empty(write.HeldProtectionIds);
    }

    [Fact]
    public void Catch_handler_does_not_hold_a_lock_taken_inside_the_try()
    {
        var run = Action("""
            try
            {
                System.Threading.Monitor.Enter(Gates.G);
                State.Other = 1;
            }
            catch
            {
                State.Value = 1;
            }
            """);

        Assert.Equal([G_ID], run.Single("Other", AccessOperation.Write).HeldProtectionIds);
        Assert.Empty(run.Single("Value", AccessOperation.Write).HeldProtectionIds);
    }

    [Fact]
    public void Finally_body_does_not_hold_a_lock_taken_inside_the_try()
    {
        var write = Action("""
            try
            {
                System.Threading.Monitor.Enter(Gates.G);
            }
            finally
            {
                State.Value = 1;
                System.Threading.Monitor.Exit(Gates.G);
            }
            """).Single("Value", AccessOperation.Write);

        Assert.Empty(write.HeldProtectionIds);
    }

    [Fact]
    public void Lock_on_a_boxed_value_is_not_a_single_object()
    {
        var write = Action("lock ((object)Gates.Counter) { State.Value = 1; }").Single("Value", AccessOperation.Write);

        Assert.Empty(write.HeldProtectionIds);
    }

    [Fact]
    public void Lambda_inside_a_lock_starts_with_nothing_held()
    {
        var run = Action("""
            Action write;
            lock (Gates.G)
            {
                write = () => State.Value = 1;
                State.Other = 1;
            }
            write();
            """);

        Assert.Empty(run.Single("Value", AccessOperation.Write).HeldProtectionIds);
        Assert.Equal([G_ID], run.Single("Other", AccessOperation.Write).HeldProtectionIds);
    }

    [Fact]
    public void Static_non_readonly_lock_is_held_but_not_a_single_object()
    {
        var write = Action("Gates.Mutable = new object(); lock (Gates.Mutable) { State.Value = 1; }").Single("Value", AccessOperation.Write);

        Assert.Empty(write.HeldProtectionIds);
        Assert.Equal(["alloc:Gates..cctor()#object#3, alloc:LockController.Post(bool)#object (not one object per process)"], write.HeldProtection);
    }

    [Fact]
    public void Per_request_lock_is_held_but_not_a_single_object()
    {
        var write = Action("lock (this) { State.Value = 1; }").Single("Value", AccessOperation.Write);

        Assert.Empty(write.HeldProtectionIds);
        Assert.Single(write.HeldProtection);
    }

    [Fact]
    public void Lock_on_this_in_a_hosted_service_registered_once_is_a_single_object()
    {
        var write = Analyze(Shared + Worker("lock (this) { State.Value = 1; }") +
                            Startup("services.AddHostedService<Worker>();")).Single("Value", AccessOperation.Write);

        Assert.Equal(["scope:Fixture|di|Microsoft.Extensions.Hosting.Abstractions:Microsoft.Extensions.Hosting.IHostedService|Fixture:Worker@Singleton|singleton"],
                     write.HeldProtectionIds);
    }

    [Fact]
    public void Lock_on_this_in_a_hosted_service_registered_twice_is_not_a_single_object()
    {
        var write = Analyze(Shared + Worker("lock (this) { State.Value = 1; }") +
                            Startup("services.AddHostedService<Worker>(); services.AddSingleton<IHostedService, Worker>();"))
                    .Single("Value", AccessOperation.Write);

        Assert.Empty(write.HeldProtectionIds);
        Assert.Single(write.HeldProtection);
    }

    [Fact]
    public void Lock_on_a_bound_singleton_member_is_a_single_object()
    {
        var write = Analyze(Shared + """
            public sealed class Ledger { public string? LastEntry; }
            public class LedgerController : ControllerBase
            {
                private readonly Ledger _ledger;
                public LedgerController(Ledger ledger) => _ledger = ledger;
                public void Post() { lock (_ledger) { _ledger.LastEntry = "x"; } }
            }
            """ + Startup("services.AddSingleton<Ledger>();")).Single("LastEntry", AccessOperation.Write);

        Assert.Equal(["scope:Fixture|di|Fixture:Ledger|Fixture:Ledger@Singleton|singleton"], write.HeldProtectionIds);
    }

    [Fact]
    public void Lock_on_a_new_object_is_held_but_not_single_object()
    {
        var run = Action("lock (new object()) { State.Value = 1; } State.Other = 1;");

        var inside = run.Single("Value", AccessOperation.Write);
        var held = Assert.Single(inside.HeldProtection);
        Assert.Contains("not one object per process", held, StringComparison.Ordinal);
        Assert.Empty(inside.HeldProtectionIds);
        Assert.Empty(run.Single("Other", AccessOperation.Write).HeldProtection);

        var nested = Action("lock (Gates.G) { lock (new object()) { State.Other = 1; } State.Value = 1; }");
        Assert.Equal([G_ID], nested.Single("Value", AccessOperation.Write).HeldProtectionIds);
        Assert.Equal(2, nested.Single("Other", AccessOperation.Write).HeldProtection.Count);
    }

    [Fact]
    public void Lock_on_a_local_is_held_but_not_single_object()
    {
        var run = Action("var gate = condition ? Gates.G : Gates.H; lock (gate) { State.Value = 1; } State.Other = 1;");

        var inside = run.Single("Value", AccessOperation.Write);
        Assert.Contains("not one object per process", Assert.Single(inside.HeldProtection), StringComparison.Ordinal);
        Assert.Empty(inside.HeldProtectionIds);
        Assert.Empty(run.Single("Other", AccessOperation.Write).HeldProtection);
    }

    [Fact]
    public void Lock_on_an_unresolved_local_adds_nothing_that_can_suppress()
    {
        var write = Action("object gate = new object(); lock (gate) { State.Value = 1; }").Single("Value", AccessOperation.Write);

        Assert.Empty(write.HeldProtectionIds);
        Assert.Contains("not one object per process", Assert.Single(write.HeldProtection), StringComparison.Ordinal);
    }

    [Fact]
    public void Early_return_inside_a_lock_keeps_the_rest_of_the_body_held()
    {
        var write = Action("lock (Gates.G) { if (condition) return; State.Value = 1; }").Single("Value", AccessOperation.Write);

        Assert.Equal([G_ID], write.HeldProtectionIds);
    }

    private static EngineRun Action(string body) => Analyze(Shared + $$"""
        public class LockController : ControllerBase
        {
            public void Post(bool condition)
            {
                {{body}}
            }
        }
        """ + Startup());

    private static string Worker(string body) => $$"""
        public sealed class Worker : BackgroundService
        {
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
                return Task.CompletedTask;
            }
        }
        """;
}
