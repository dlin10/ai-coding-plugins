using System.Collections.Concurrent;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>A <c>Nullable&lt;S&gt;</c> is the value it holds in CoreLib's own <c>Nullable&lt;T&gt;</c> bodies too (R11): each cell runs a
/// driver over the generation's decompiled CoreLib that calls one member of <c>System.Nullable&lt;T&gt;</c> on a <c>Nullable</c> of a probe the
/// driver created and holds in a local, and reads the heap that run gives, whatever the member's answer. Only under <c>CH_MODEL_EVALS=1</c>.</summary>
[Collection(ModelEvalTests.COLLECTION)]
public sealed class NullableHeldValueMatrixTests
{
    private const string CORELIB = "System.Private.CoreLib";
    private const string SCOPE_ID = "nullable-held-value";

    /// <summary>The member of <c>Nullable&lt;T&gt;</c> the driver calls.</summary>
    public enum Member
    {
        /// <summary><c>new Nullable&lt;T&gt;(probe)</c>, CoreLib's constructor body, followed by a <c>GetHashCode()</c> on what it made.</summary>
        Constructor,
        HasValue,
        Value,
        GetValueOrDefault,

        /// <summary><c>GetValueOrDefault(T)</c>.</summary>
        GetValueOrDefaultWithDefault,
        GetHashCode,

        /// <summary><c>Equals(object)</c>.</summary>
        Equals,
        ToString
    }

    /// <summary>The probe the <c>Nullable</c> holds.</summary>
    public enum Probe
    {
        /// <summary>The struct probe, holding two objects, its <c>GetHashCode</c>, <c>Equals</c> and <c>ToString</c> read witnesses.</summary>
        HoldsObjects,

        /// <summary>A struct probe with no reference field and the same three overrides.</summary>
        HoldsNothing
    }

    /// <summary>A cell of the matrix.</summary>
    /// <param name="Member">The member called.</param>
    /// <param name="Probe">The probe held.</param>
    private sealed record Cell(Member Member, Probe Probe)
    {
        public override string ToString() => $"{Member}/{Probe}";
    }

    /// <summary>The cells whose outcome is known to be wider than the requirement asks: none.</summary>
    private static readonly Cell[] KnownWider = [];

    private static readonly ConcurrentDictionary<Cell, Lazy<(ScopeRun Run, Compilation Driver)>> Runs = new();
    private static readonly Dictionary<(Compilation Compilation, string BodyId, LibraryModels Models), IrLoweredMethod?> Lowered = new();

    private static IEnumerable<Cell> Cells() =>
        from member in Enum.GetValues<Member>()
        from probe in Enum.GetValues<Probe>()
        select new Cell(member, probe);

    public static TheoryData<Member, Probe> Rows()
    {
        var rows = new TheoryData<Member, Probe>();
        foreach (var cell in Cells())
            rows.Add(cell.Member, cell.Probe);
        return rows;
    }

    [RequiresModelEvalsTheory]
    [MemberData(nameof(Rows))]
    public void Every_cell_runs_on_the_held_value(Member member, Probe probe)
    {
        var cell = new Cell(member, probe);
        var (run, driver) = RunOf(cell);
        var heap = run.Heap!;
        var failures = new List<string>();
        void Fail(bool holds, string what)
        {
            if (!holds)
                failures.Add(what);
        }

        var probeType = $"{DriverSynthesizer.ASSEMBLY}:{ProbeName(probe)}";
        var instances = Instances(run, Body(member));
        Fail(instances.Length != 0, $"{Body(member)} has no instance");
        foreach (var instance in instances)
            Fail(instance.Receivers.Count != 0 && instance.Receivers.All(receiver => heap.Regions[receiver].TypeKey == probeType),
                 $"{instance.Id} runs on {string.Join(", ", instance.Receivers.Select(receiver => heap.Regions[receiver].TypeKey))}, not on the probe");

        // What the member gives back: a value of a struct is a copy (R2), so Value and GetValueOrDefault give back a copy of the region they
        // run on — a region of its own, of the probe's type, whose reference fields hold the objects the receiver's hold — and
        // GetValueOrDefault(T) such a copy or a copy of its argument; HasValue gives back no object.
        var objects = heap.Regions.Values.Where(region => region is { Kind: HeapRegionKind.Allocation } &&
                                                          region.TypeKey == $"{DriverSynthesizer.ASSEMBLY}:Probe_T_Object")
                          .Select(region => region.Identity).ToHashSet();
        foreach (var instance in instances)
        {
            var returned = instance.Summary.Returns.SelectMany(@return => @return.Values).SelectMany(value => heap.Resolve(instance.Id, value)).ToHashSet();
            switch (member)
            {
                case Member.Value or Member.GetValueOrDefault or Member.GetValueOrDefaultWithDefault:
                {
                    var argument = member == Member.GetValueOrDefaultWithDefault
                        ? instance.Parameters.GetValueOrDefault(0) ?? new HashSet<string>()
                        : new HashSet<string>();
                    Fail(returned.Count != 0 && returned.All(region => !instance.Receivers.Contains(region) && !argument.Contains(region) &&
                                                                        heap.Regions[region].TypeKey == probeType),
                         $"{instance.Id} gives back {string.Join(", ", returned)}, not a copy of the probe of its own");
                    // A copy's reference fields hold what its sources' hold, and nothing else.
                    var sources = instance.Receivers.Concat(argument).ToArray();
                    foreach (var copy in returned)
                    {
                        foreach (var slot in heap.FieldsOf(copy))
                            Fail(heap.PointsTo(copy, slot).IsSubsetOf(sources.SelectMany(source => heap.PointsTo(source, slot))),
                                 $"{copy} holds at {slot} an object neither the receiver nor the argument holds");
                    }
                    foreach (var receiver in instance.Receivers)
                    {
                        foreach (var slot in heap.FieldsOf(receiver))
                            Fail(returned.Any(copy => heap.PointsTo(copy, slot).IsSupersetOf(heap.PointsTo(receiver, slot))),
                                 $"no copy {instance.Id} gives back holds at {slot} what its receiver {receiver} holds");
                    }
                    // On HoldsObjects the copy holds both objects the probe holds.
                    if (probe == Probe.HoldsObjects)
                    {
                        var held = instance.Receivers.SelectMany(receiver => heap.FieldsOf(receiver).SelectMany(slot => heap.PointsTo(receiver, slot)))
                                           .Where(objects.Contains).ToHashSet();
                        var copied = returned.SelectMany(copy => heap.FieldsOf(copy).SelectMany(slot => heap.PointsTo(copy, slot))).ToHashSet();
                        Fail(held.Count == 2 && copied.IsSupersetOf(held),
                             $"{instance.Id}'s receiver holds [{string.Join(", ", held)}], the copy it gives back [{string.Join(", ", copied)}]");
                    }
                    break;
                }
                case Member.HasValue:
                    Fail(returned.Count == 0, $"{instance.Id} gives back {string.Join(", ", returned)}");
                    break;
            }
        }

        // The probe's override runs on the probe's region, and on HoldsObjects its witness reads both objects the probe holds.
        if (Override(member) is { } name)
        {
            var body = IrLowering.RootBodyId(driver.GetTypeByMetadataName(ProbeName(probe))!.GetMembers(name).OfType<IMethodSymbol>().Single());
            var overrides = heap.Instances.Values.Where(instance => instance.BodyId == body).ToArray();
            Fail(overrides.Length != 0 && overrides.All(instance => instance.Receivers.All(receiver => heap.Regions[receiver].TypeKey == probeType)),
                 $"{ProbeName(probe)}.{name} does not run on the probe");
            if (probe == Probe.HoldsObjects)
            {
                var (first, second) = WitnessFields(name);
                var read = new[] { first, second }.Select(field => heap.PointsTo($"static:{DriverSynthesizer.ASSEMBLY}:Witnessed",
                                                                                   HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, "Witnessed", field)))
                                                  .ToArray();
                Fail(read.All(regions => regions.Overlaps(objects)) && read[0].Union(read[1]).Intersect(objects).Count() == 2,
                     $"the witness of {name} does not read both objects: {string.Join(" / ", read.Select(regions => string.Join(", ", regions)))}");
            }
        }

        Assert.True(failures.Count == 0, $"{cell}:\n{string.Join("\n", failures)}");
    }

    [RequiresModelEvalsTheory]
    [MemberData(nameof(Rows))]
    public void No_nullable_member_call_is_dead(Member member, Probe probe)
    {
        var cell = new Cell(member, probe);
        var (run, _) = RunOf(cell);
        var heap = run.Heap!;
        // Every call of the member's instance, and of Nullable.GetHashCode after the constructor, binds a callee: none is dropped as a call on
        // an incompatible receiver.
        var bodies = member == Member.Constructor ? new[] { Body(member), Body(Member.GetHashCode) } : [Body(member)];
        foreach (var instance in bodies.SelectMany(body => Instances(run, body)))
        {
            foreach (var call in instance.Summary.Calls)
                Assert.True(heap.Edges.Any(edge => edge.CallerInstance == instance.Id && edge.OperationId == call.OperationId),
                            $"{cell}: {call.Callee} in {instance.Id} binds nothing");
        }

        // No cell stores into a field of Nullable<T>: the constructor's writes of value and hasValue are the creation's whole write.
        Assert.DoesNotContain(run.Reachable.Bodies.Values.SelectMany(body => body.Blocks).SelectMany(block => block.Operations).OfType<IrStoreFieldOperation>(),
                              store => store.Field.ContainingType.StartsWith("System.Nullable<", StringComparison.Ordinal) ||
                                       store.Field.ContainingType.StartsWith("T?", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_value_of_every_axis_occurs()
    {
        var cells = Cells().ToArray();
        Assert.Empty(KnownWider);
        Assert.Equal(16, cells.Length);
        Assert.All(Enum.GetValues<Member>(), value => Assert.Contains(cells, cell => cell.Member == value));
        Assert.All(Enum.GetValues<Probe>(), value => Assert.Contains(cells, cell => cell.Probe == value));
    }

    // ---- the run ----

    private static string ProbeName(Probe probe) => probe == Probe.HoldsObjects ? "Probe_T" : "Probe_N";

    /// <summary>The probe override a member's body calls, or the one called on what the constructor made; null for a member that calls none.</summary>
    /// <param name="member">The member.</param>
    private static string? Override(Member member) => member switch
    {
        Member.Constructor or Member.GetHashCode => "GetHashCode",
        Member.Equals => "Equals",
        Member.ToString => "ToString",
        _ => null
    };

    /// <summary>The two <c>Witnessed</c> fields an override's witness stores the probe's <c>Ref0</c> and <c>Ref1</c> into.</summary>
    /// <param name="name">The override.</param>
    private static (string First, string Second) WitnessFields(string name) => name switch
    {
        "GetHashCode" => ("R0", "R1"),
        "Equals" => ("R2", "R3"),
        _ => ("R5", "R6")
    };

    private static MethodInstance[] Instances(ScopeRun run, string body) =>
        run.Heap!.Instances.Values.Where(instance => instance.BodyId == body).ToArray();

    /// <summary>The body id of the member of CoreLib's <c>Nullable&lt;T&gt;</c>.</summary>
    /// <param name="member">The member.</param>
    private static string Body(Member member)
    {
        var nullable = CoreLibGenerationEvalTests.Library.Value.Compilation!.GetSpecialType(SpecialType.System_Nullable_T);
        IMethodSymbol Named(string name, int parameters) =>
            nullable.GetMembers(name).OfType<IMethodSymbol>().Single(method => method.Parameters.Length == parameters);
        return IrLowering.RootBodyId(member switch
        {
            Member.Constructor => nullable.InstanceConstructors.Single(constructor => constructor.Parameters.Length == 1),
            Member.HasValue => Named("get_HasValue", 0),
            Member.Value => Named("get_Value", 0),
            Member.GetValueOrDefault => Named("GetValueOrDefault", 0),
            Member.GetValueOrDefaultWithDefault => Named("GetValueOrDefault", 1),
            Member.GetHashCode => Named("GetHashCode", 0),
            Member.Equals => Named("Equals", 1),
            Member.ToString => Named("ToString", 0),
            _ => throw new ArgumentOutOfRangeException(nameof(member), member, null)
        });
    }

    private static (ScopeRun Run, Compilation Driver) RunOf(Cell cell)
    {
        var result = Runs.GetOrAdd(cell, key => new Lazy<(ScopeRun, Compilation)>(() => Execute(key))).Value;
        Assert.False(result.Run.Stopped, $"{cell}: the run stopped at {result.Run.StoppedAtReachableBodies} bodies");
        return result;
    }

    /// <summary>Runs the cell's driver over decompiled CoreLib as CoreLib's generation runs its drivers.</summary>
    /// <param name="cell">The cell.</param>
    private static (ScopeRun Run, Compilation Driver) Execute(Cell cell)
    {
        var library = CoreLibGenerationEvalTests.Library.Value.Compilation!;
        var tree = CSharpSyntaxTree.ParseText(Source(cell), new CSharpParseOptions(LanguageVersion.Preview), $"{DriverSynthesizer.ASSEMBLY}/{DriverSynthesizer.DRIVER_TYPE}.cs");
        var driver = CSharpCompilation.Create(DriverSynthesizer.ASSEMBLY, [tree], library.References.Append(library.ToMetadataReference()),
                                              new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));
        Assert.Empty(driver.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var models = new LibraryModels(LibraryModels.BuiltIn.Members.Where(model => model.Assemblies.All(range => range.AssemblyName != CORELIB)),
                                       LibraryModels.BuiltIn.ImmutableTypes.Where(type => type.Assemblies.All(range => range.AssemblyName != CORELIB)));
        var reachability = new ReachabilityRules(DispatchRule.ConstructedTypes, new HashSet<string>(StringComparer.Ordinal) { $"{CORELIB}:System.SR" })
        {
            OpenWorldAssemblies = new HashSet<string>(StringComparer.Ordinal) { DriverSynthesizer.ASSEMBLY }
        };
        lock (Lowered)
        {
            var run = ScopePipeline.Run(SCOPE_ID, [library, driver], [], Path.GetTempPath(), new ProviderRegistry([new DriverRootProvider()]), models,
                                        AnalysisLimits.Default, Lowered, 1500, CancellationToken.None, reachability: reachability);
            return (run, driver);
        }
    }

    /// <summary>The driver of a cell: the probe struct, the class of the objects it holds, and <c>V_Call</c>, which makes the probe, holds a
    /// <c>Nullable</c> of it in a local and calls the member.</summary>
    /// <param name="cell">The cell.</param>
    private static string Source(Cell cell)
    {
        var probe = ProbeName(cell.Probe);
        var holds = cell.Probe == Probe.HoldsObjects;
        var make = holds ? $"new {probe}() {{ Ref0 = new Probe_T_Object(), Ref1 = new Probe_T_Object() }}" : $"new {probe}()";
        var other = holds ? $"new {probe}() {{ Ref0 = new Probe_T_Object() }}" : $"new {probe}()";
        var nullable = $"global::System.Nullable<{probe}>";
        var call = cell.Member switch
        {
            Member.Constructor => $"var n = new {nullable}(p); Keep.I = n.GetHashCode();",
            Member.HasValue => $"{nullable} n = p; Keep.B = n.HasValue;",
            Member.Value => $"{nullable} n = p; Keep.V = n.Value;",
            Member.GetValueOrDefault => $"{nullable} n = p; Keep.V = n.GetValueOrDefault();",
            Member.GetValueOrDefaultWithDefault => $"{nullable} n = p; Keep.V = n.GetValueOrDefault({other});",
            Member.GetHashCode => $"{nullable} n = p; Keep.I = n.GetHashCode();",
            Member.Equals => $"{nullable} n = p; Keep.B = n.Equals(new object());",
            Member.ToString => $"{nullable} n = p; Keep.S = n.ToString();",
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null)
        };
        string Read(string first, string second) => holds ? $"Witnessed.{first} = this.Ref0; Witnessed.{second} = this.Ref1;" : "";
        var fields = holds ? "public object Ref0; public object Ref1; public int Int0;" : "public int Int0;";
        return $$"""
            #pragma warning disable
            public static class ModelDriver
            {
                public static void V_Setup() { }
                public static void V_Call() { var p = {{make}}; {{call}} }
            }
            public static class Keep { public static {{probe}} V; public static int I; public static bool B; public static string S; }
            public static class Witnessed { public static object R0; public static object R1; public static object R2; public static object R3; public static object R4; public static object R5; public static object R6; }
            public sealed class Probe_T_Object { }
            public struct {{probe}}
            {
                {{fields}}
                public override int GetHashCode() { {{Read("R0", "R1")}} return 0; }
                public override bool Equals(object p0) { {{Read("R2", "R3")}} Witnessed.R4 = p0; return false; }
                public override string ToString() { {{Read("R5", "R6")}} return null; }
            }
            """;
    }
}
