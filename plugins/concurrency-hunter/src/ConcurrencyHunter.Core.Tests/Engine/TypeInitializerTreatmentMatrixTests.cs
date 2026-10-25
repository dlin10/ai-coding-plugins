using System.Collections.Concurrent;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class TypeInitializerTreatmentMatrixTests
{
    private enum Binding
    {
        NonGeneric, ClosedOverLibrary, ClosedOverDriver, OverOpenParameter, NestedDriver, NestedLibrary,
        ArrayOfDriver, NestedDriverInArray, NestedDriverInArrays, NestedLibraryInArray
    }

    private enum Trigger
    {
        StaticFieldRead, StaticFieldWrite, StaticMethodCall, Construction, StaticDelegateCreation, ContainerConstruction, ComparerSelection
    }

    private sealed record Cell(TypeInitializerTreatment Treatment, Binding Binding, Trigger Trigger);

    private static IEnumerable<Cell> Cells() =>
        from treatment in Enum.GetValues<TypeInitializerTreatment>()
        from binding in Enum.GetValues<Binding>()
        from trigger in Enum.GetValues<Trigger>()
        let cell = new Cell(treatment, binding, trigger)
        where Allowed(cell)
        select cell;

    private static bool Allowed(Cell cell) => cell.Trigger switch
    {
        // A comparer selection always constructs a generic comparer and requires a closed factory binding.
        Trigger.ComparerSelection => cell.Binding is not (Binding.NonGeneric or Binding.OverOpenParameter),
        // DI cannot construct an open service type.
        Trigger.ContainerConstruction => cell.Binding != Binding.OverOpenParameter,
        _ => true
    };

    private static bool Runs(Cell cell) => cell.Treatment == TypeInitializerTreatment.Run || cell.Binding is
        Binding.ClosedOverDriver or Binding.OverOpenParameter or Binding.NestedDriver or Binding.ArrayOfDriver or
        Binding.NestedDriverInArray or Binding.NestedDriverInArrays;

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        var cells = Cells().ToArray();
        Assert.Equal(Enum.GetValues<TypeInitializerTreatment>(), cells.Select(cell => cell.Treatment).Distinct().Order());
        Assert.Equal(Enum.GetValues<Binding>(), cells.Select(cell => cell.Binding).Distinct().Order());
        Assert.Equal(Enum.GetValues<Trigger>(), cells.Select(cell => cell.Trigger).Distinct().Order());
    }

    [Fact]
    public void Every_cell_gets_its_expected_treatment()
    {
        foreach (var cell in Cells())
        {
            if (cell.Trigger == Trigger.ContainerConstruction)
            {
                // Observe activation from DI alone before the reader used by the remaining observations adds a first-use edge.
                var (construction, type) = Run(cell, readAfterConstruction: false);
                var expected = Runs(cell) || cell.Treatment == TypeInitializerTreatment.Inert;
                var activated = construction.Heap!.TypeInitializers.Where(row => construction.Program.Type(row.TypeKey)?.TypeKey == type).ToArray();
                Assert.True(expected == (activated.Length > 0), $"{cell}: container alone activates");
                Assert.All(activated, row =>
                {
                    Assert.NotEmpty(row.TriggeringRegions);
                    Assert.Equal(!Runs(cell), row.IsInert);
                });
            }
            var (run, definition) = Run(cell);
            Assert.False(run.Stopped);
            var runs = Runs(cell);
            var present = runs || cell.Treatment == TypeInitializerTreatment.Inert;
            var initializerBody = run.Program.Methods.Single(method => method.ContainingTypeKey == definition &&
                                                                      method.Kind == ProgramMethodKind.TypeInitializer).MethodId;
            Assert.True(present == run.Reachable.ReachedBodies.ContainsKey(initializerBody), $"{cell}: reached");
            var rows = run.Heap!.TypeInitializers.Where(row => run.Heap.Instances.GetValueOrDefault(row.InstanceId)?.BodyId == initializerBody).ToArray();
            Assert.True(present == (rows.Length > 0), $"{cell}: activated");
            var executions = run.Executions!.Executions.Where(execution => execution.Kind == ExecutionKind.TypeInitializer &&
                                                                         rows.Any(row => row.TypeKey == execution.Subject)).ToArray();
            Assert.True(present == (executions.Length > 0), $"{cell}: execution");
            Assert.All(rows, row => Assert.Equal(!runs, row.IsInert));
            Assert.All(executions, execution => Assert.Equal(!runs, run.Executions.IsInert(execution.Id)));

            var projected = run.Executions.ExecutionAccesses().Any(access => access.Access.Field.Name == "Count" &&
                                                                           access.Access.Kind == SummaryAccessKind.Store);
            var collected = run.Collection!.Accesses.Any(access => access.Resource.Member.Name == "Count" && access.Operation is AccessOperation.Write or AccessOperation.ReadModifyWrite);
            Assert.True(runs == projected, $"{cell}: projected accesses");
            Assert.True(runs == collected, $"{cell}: interprocedural accesses");

            Assert.DoesNotContain(run.Executions.Accesses, access => access.Executions.Any(run.Executions.IsInert));
            Assert.DoesNotContain(run.Collection.Accesses, access => run.Executions.IsInert(access.ExecutionId));
            Assert.Contains(run.Executions.ExecutionAccesses(), access => access.Access.Field.Name == "SharedCount" &&
                                                                         run.Executions.Execution(access.ExecutionId).RootId is not null);
            Assert.Contains(run.Collection.Accesses, access => access.Resource.Member.Name == "SharedCount" &&
                                                               run.Executions.Execution(access.ExecutionId).RootId is not null);

            var allocations = run.Heap.Regions.Values.Where(region => region.Kind == HeapRegionKind.Allocation &&
                                                                      region.SiteBodyId == initializerBody).Select(region => region.Identity).ToHashSet();
            var stored = run.Heap.Instances.Values.Any(instance => instance.Summary.Accesses.Where(access => access.Field is { IsStatic: true, Name: "Value" })
                                          .Any(access => run.Heap.PointsTo(run.Heap.StaticRegionOf(instance.Id, access.Field), "Value").Any(allocations.Contains)));
            Assert.True(present == stored, $"{cell}: static points to initializer allocation");
            var subjects = rows.Select(row => run.Heap.Regions.TryGetValue($"static:{row.TypeKey}", out var region)
                                                ? region.Display : run.Heap.Instances[row.InstanceId].BodyId).ToArray();
            var flow = run.Collection.Accesses.Any(access => access.CodeFlow.Any(step => step.Kind == "construction" &&
                subjects.Any(subject => step.Text == $"constructs {subject}" || step.Text.StartsWith($"constructs {subject} in ", StringComparison.Ordinal))));
            Assert.True(runs == flow, $"{cell}: construction code flow");
            var discovered = executions.Any(execution => run.Collection.Accesses.Any(access => access.ExecutionId == execution.Id && run.Roots.Any(root => root.StableRootId == access.PathRoot.RootId)));
            Assert.True(runs == discovered, $"{cell}: discovery");
            var gap = run.Collection.Coverage.Gaps.Any(gap => gap.Callee.Contains("Unknown", StringComparison.Ordinal) && gap.Roots > 0);
            Assert.True(runs == gap, $"{cell}: initializer gap reaches root");
            var common = run.Heap.Instances.Values.Where(instance => instance.BodyId.EndsWith("M:Shared.Common", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(common);
            Assert.Contains(common, instance => run.Executions.InstanceExecutions[instance.Id].Any(id => run.Executions.Executions.Any(execution => execution.Id == id && execution.RootId is not null)));
            Assert.True(present == common.Any(instance => run.Executions.InstanceExecutions[instance.Id].Any(id => executions.Any(execution => execution.Id == id)) &&
                                                        run.Executions.InstanceExecutions[instance.Id].Any(id => run.Executions.Executions.Any(execution => execution.Id == id && execution.RootId is not null))),
                        $"{cell}: shared helper membership");
        }
    }

    [Fact]
    public void An_inert_initializers_object_is_one_object_per_process()
    {
        foreach (var treatment in new[] { TypeInitializerTreatment.Run, TypeInitializerTreatment.Inert })
        {
            var (run, _) = Run(new Cell(treatment, Binding.NonGeneric, Trigger.StaticFieldRead));
            var row = Assert.Single(run.Heap!.TypeInitializers, row => row.TypeKey == "Library:Registry");
            var value = Assert.Single(run.Heap.PointsTo($"static:{row.TypeKey}", "Value"));
            Assert.True(run.Executions!.IsSingleObject(value));
        }
    }

    [Fact]
    public void A_merged_instance_runs_if_any_of_its_types_runs_its_initializer()
    {
        var run = TypeInitializerTreatmentFixture.Run(Library("Registry<T>"),
            "GC.KeepAlive(Registry<int>.Value); GC.KeepAlive(Registry<Driver.D>.Value); Shared.Enter();",
            TypeInitializerTreatment.Inert, limits: AnalysisLimits.Default with { MaxContextsPerMethod = 0 });
        var rows = run.Heap!.TypeInitializers.Where(row => row.TypeKey.Contains(":Registry<", StringComparison.Ordinal)).ToArray();
        Assert.True(rows.Length >= 2);
        Assert.Single(rows.Select(row => row.InstanceId).Distinct());
        Assert.All(rows, row => Assert.False(row.IsInert));
        Assert.All(run.Executions!.Executions.Where(execution => rows.Any(row => row.TypeKey == execution.Subject)), execution => Assert.False(execution.IsInert));
        Assert.Contains(run.Collection!.Accesses, access => access.Resource.Member.Name == "Count" && access.Operation is AccessOperation.Write or AccessOperation.ReadModifyWrite);
    }

    [Fact]
    public void A_merged_running_initializer_still_has_a_non_running_closure_to_scan()
    {
        var run = TypeInitializerTreatmentFixture.Run(Library("Registry<T>"),
            "GC.KeepAlive(Registry<int>.Value); GC.KeepAlive(Registry<Driver.D>.Value); Shared.Enter();",
            TypeInitializerTreatment.Inert, limits: AnalysisLimits.Default with { MaxContextsPerMethod = 0 });
        var rows = run.Heap!.TypeInitializers.Where(row => row.TypeKey.Contains(":Registry<", StringComparison.Ordinal)).ToArray();
        Assert.Single(rows.Select(row => row.InstanceId).Distinct());
        Assert.All(rows, row => Assert.False(row.IsInert));
        var candidate = Assert.Single(run.Reachable.TypeInitializers, candidate => candidate.TypeKey.Contains(":Registry", StringComparison.Ordinal));
        Assert.Contains(candidate.ClosedTypeKeys, key => !ReachableSet.RunsTypeInitializer(run.Program, key, TypeInitializerTreatment.Off,
                                                                                        run.Reachable.OpenWorldAssemblies));
        Assert.Contains(candidate.ClosedTypeKeys, key => ReachableSet.RunsTypeInitializer(run.Program, key, TypeInitializerTreatment.Off,
                                                                                       run.Reachable.OpenWorldAssemblies));
        Assert.Contains(ModelGenerator.InitializerReachedBodies([run], new GenerationChecks(CancellationToken.None)),
                        reached => reached.Initializer == candidate.BodyId && reached.Body.BodyId == candidate.BodyId);
    }

    [Fact]
    public void A_delegate_an_inert_initializer_hands_to_unseen_code_has_it_as_handing_execution()
    {
        var run = TypeInitializerTreatmentFixture.Run("""
            using System;
            using System.Runtime.CompilerServices;
            public static class Other { public static object? Value; }
            public class Registry
            {
                public static object Value;
                static Registry() { Value = new object(); Unknown(() => Other.Value = new object()); }
                [MethodImpl(MethodImplOptions.InternalCall)] private static extern void Unknown(Action action);
            }
            """, "GC.KeepAlive(Registry.Value);", TypeInitializerTreatment.Inert);
        var initializer = Assert.Single(run.Executions!.Executions, execution => execution.Kind == ExecutionKind.TypeInitializer);
        Assert.True(initializer.IsInert);
        var unknown = Assert.Single(run.Executions.Executions, execution => execution.Kind == ExecutionKind.UnknownDelegateCall);
        var access = Assert.Single(run.Collection!.Accesses, access => access.ExecutionId == unknown.Id && access.Operation is AccessOperation.Write or AccessOperation.ReadModifyWrite &&
                                                                      access.Resource.Member.Name == "Value");
        Assert.False(unknown.OverlapsItself);
        var handing = Assert.Single(run.Heap!.DelegateHandoffs);
        Assert.All(handing.Sites, site => Assert.Contains(initializer.Id, run.Executions.InstanceExecutions[site.CallerInstance]));
    }

    private static (ScopeRun Run, string Definition) Run(Cell cell, bool readAfterConstruction = true)
    {
        var argument = cell.Binding switch
        {
            Binding.ClosedOverLibrary => "int",
            Binding.ClosedOverDriver => "Driver.D",
            Binding.OverOpenParameter => "T",
            Binding.NestedDriver => "System.Collections.Generic.List<Driver.D>",
            Binding.NestedLibrary => "System.Collections.Generic.List<int>",
            Binding.ArrayOfDriver => "Driver.D[]",
            Binding.NestedDriverInArray => "System.Collections.Generic.List<Driver.D>[]",
            Binding.NestedDriverInArrays => "System.Collections.Generic.List<Driver.D>[,][]",
            Binding.NestedLibraryInArray => "System.Collections.Generic.List<int>[]",
            _ => ""
        };
        var type = argument.Length == 0 ? "Registry" : $"Registry<{argument}>";
        var trigger = cell.Trigger switch
        {
            Trigger.StaticFieldRead => $"GC.KeepAlive({type}.Value);",
            Trigger.StaticFieldWrite => $"{type}.Sink = new object();",
            Trigger.StaticMethodCall => $"{type}.Touch();",
            Trigger.Construction => $"GC.KeepAlive(new {type}());",
            Trigger.StaticDelegateCreation => $"Action action = {type}.Touch; action();",
            Trigger.ContainerConstruction => "GC.KeepAlive(registry);" + (readAfterConstruction ? $" GC.KeepAlive({type}.Value);" : ""),
            Trigger.ComparerSelection => $"GC.KeepAlive(Selection.Make<{argument}>());",
            _ => throw new ArgumentOutOfRangeException()
        };
        var open = cell.Binding == Binding.OverOpenParameter;
        var extra = open ? $"private static void Open<T>() {{ {trigger} Shared.Enter(); }}" : "";
        var body = open ? "Open<int>();" : trigger + " Shared.Enter();";
        var comparer = cell.Trigger == Trigger.ComparerSelection;
        var library = comparer ? Comparers : Library(cell.Binding == Binding.NonGeneric ? "Registry" : "Registry<T>");
        var run = TypeInitializerTreatmentFixture.Run(library, body, cell.Treatment, extra,
            cell.Trigger == Trigger.ContainerConstruction ? type : null, comparer,
            open ? AnalysisLimits.Default with { MaxContextsPerMethod = 0 } : null);
        var definition = comparer
            ? run.Program.Type(DefaultComparers.Of(run.Program, ConcurrencyHunter.Ir.IrComparerKind.Equality,
                               Assert.Single(run.Reachable.DefaultComparerTypes).TypeKey))!.TypeKey
            : run.Program.Types.Single(type => type.DisplayName == (cell.Binding == Binding.NonGeneric ? "Registry" : "Registry<T>")).TypeKey;
        return (run, definition);
    }

    private static string Library(string declaration) => $$"""
        using System;
        using System.Runtime.CompilerServices;
        public sealed class Payload { public object? Data; }
        public static class Other { public static int Count; public static int SharedCount; }
        public static class Shared { public static void Enter() => Common(); public static void Common() { Other.SharedCount++; } }
        public class {{declaration}}
        {
            public static Payload Value;
            public static object? Sink;
            static Registry() { Value = new Payload(); Other.Count++; Shared.Enter(); Unknown(Value); }
            public static void Touch() { }
            [MethodImpl(MethodImplOptions.InternalCall)] private static extern void Unknown(object value);
        }
        """;

    private const string Comparers = """
        using System;
        using System.Runtime.CompilerServices;
        public sealed class Payload { public object? Data; }
        public static class Other { public static int Count; public static int SharedCount; }
        public static class Shared { public static void Enter() => Common(); public static void Common() { Other.SharedCount++; } }
        public static class Selection
        {
            public static object Make<T>() => System.Collections.Generic.ComparerHelpers.CreateDefaultEqualityComparer(typeof(T));
        }
        namespace System.Collections.Generic
        {
            public static class ComparerHelpers
            {
                [MethodImpl(MethodImplOptions.InternalCall)] public static extern object CreateDefaultEqualityComparer(Type type);
            }
            public abstract class EqualityComparer<T> { public abstract bool Equals(T x, T y); }
            public sealed class GenericEqualityComparer<T> : EqualityComparer<T>
            {
                public static Payload Value;
                static GenericEqualityComparer() { Value = new Payload(); Other.Count++; Shared.Enter(); Unknown(Value); }
                [MethodImpl(MethodImplOptions.InternalCall)] private static extern void Unknown(object value);
                public override bool Equals(T x, T y) => true;
            }
            public sealed class ObjectEqualityComparer<T> : EqualityComparer<T>
            {
                public static Payload Value;
                static ObjectEqualityComparer() { Value = new Payload(); Other.Count++; Shared.Enter(); Unknown(Value); }
                [MethodImpl(MethodImplOptions.InternalCall)] private static extern void Unknown(object value);
                public override bool Equals(T x, T y) => true;
            }
        }
        """;
}

internal static class TypeInitializerTreatmentFixture
{
    private static readonly ConcurrentDictionary<string, Solution> Programs = new(StringComparer.Ordinal);

    internal static ScopeRun Run(string library, string body, TypeInitializerTreatment treatment, string extra = "", string? service = null,
                                 bool comparer = false, AnalysisLimits? limits = null)
    {
        var source = Usings + $$"""
            public class RootController{{(service is null ? "" : $"({service} registry)")}} : ControllerBase
            {
                public void Get() { {{body}} }
                {{extra}}
            }
            """ + Startup(service is null ? "" : $"services.AddSingleton<{service}>();");
        var key = $"{comparer}\n{library}\n{source}";
        var solution = Programs.GetOrAdd(key, _ => FixtureSolution.CreateProjects(new FixtureOptions
        {
            ProjectAssemblyNames = new Dictionary<string, string> { ["Library"] = comparer ? "System.Private.CoreLib" : "Library" },
            ProjectReferences = [("Library", "Driver"), ("Fixture", "Library"), ("Fixture", "Driver")]
        }, ("Driver", "D.cs", "namespace Driver { public sealed class D { } }"), ("Library", "Library.cs", library), ("Fixture", "Case.cs", source)));
        var scoped = Assert.Single(ProcessScopes.Discover(solution, ROOT_DIRECTORY).Scopes);
        var projects = scoped.Projects.Select(project => (Compilation: project.GetCompilationAsync().GetAwaiter().GetResult()!, ProjectFilePath: project.FilePath)).ToArray();
        return ScopePipeline.Run(scoped.Scope.Id, projects.Select(project => project.Compilation).ToArray(), projects, ROOT_DIRECTORY,
                                 ProviderRegistry.BuiltIn, LibraryModels.BuiltIn, limits ?? AnalysisLimits.Default, new(), null, CancellationToken.None,
                                 reachability: new ReachabilityRules(DispatchRule.ConstructedTypes, new HashSet<string>(StringComparer.Ordinal))
                                 {
                                     TypeInitializers = treatment,
                                     OpenWorldAssemblies = new HashSet<string>(StringComparer.Ordinal) { "Driver" }
                                 });
    }
}
