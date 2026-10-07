using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using ConcurrencyHunter.Roots;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The driver the model generator synthesizes from one member's signature (SPEC TD-034b, G-3), and the root provider that
/// makes its actions roots.</summary>
public sealed class DriverSynthesisTests
{
    private const string LIBRARY = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        namespace Lib
        {
            public interface ISink { void Put(object value); int Count { get; } event EventHandler Changed; int this[int i] { get; set; } void Flush() { } }
            public interface IMessage<T> where T : IMessage<T> { T Clone(); }
            public abstract class Handler { protected Handler() { } public abstract void Handle(object value); public virtual void Reset() { } }
            public abstract class Renderer { protected Renderer(string name) { } public abstract string Render(); public virtual int Width { get; protected set; } }
            public abstract class Runner { protected Runner() { } public virtual void Go(Action action) { action(); } }
            public sealed class FixedRunner : Runner { public FixedRunner() { } }
            public class Bag { public object Item; public virtual void Add(object item) { Item = item; } }
            public sealed class Box { public Box() { } public Box(Func<int> make) { Make = make; } public Func<int> Make; }
            public sealed class Hidden { internal Hidden() { } }
            public sealed class Opaque { private Opaque() { } public static Opaque Create() => new Opaque(); }
            public abstract class Shape { internal Shape() { } }
            public sealed class Circle : Shape { public Circle() { } }
            public sealed class Wrapper { public Wrapper(Inner inner) { } }
            public sealed class Inner { internal Inner() { } }
            public sealed class Outer { public Outer(Middle middle, Action done) { Middle = middle; } public Middle Middle; }
            public sealed class Middle { public Middle() { } public object Seed; }
            public sealed class Holder { public Holder(Func<int> f) { F = f; } public Func<int> F; public int Run() => F(); public Task RunAsync() => Task.CompletedTask; }
            public class Guarded { internal Guarded() { } public static Guarded Create() => new Guarded(); }
            public class Service { public virtual void Run(Action action) { action(); } public virtual int Other() => 0; public void Plain() { } }
            public class Special : Service { public override void Run(Action action) { } }
            public sealed class Locked { private Locked() { } public static Locked Open() => new Locked(); public void Run(Action a) { a(); } }
            public class Workshop { public void Use(IEnumerable<int> numbers, Outer outer, Action done) { } public void Close() { } }
            public sealed class Money { public static Money operator +(Money a, Func<int> b) => a; }
            public struct Pair { public object A; }
            public struct Slot { public Slot(object o) { O = o; } public object O; }
            public delegate void PairOut(out Pair pair);
            public sealed class Many
            {
                public Many(Action a) { }
                public void M00() {} public void M01() {} public void M02() {} public void M03() {} public void M04() {} public void M05() {}
                public void M06() {} public void M07() {} public void M08() {} public void M09() {} public void M10() {} public void M11() {}
                public void M12() {} public void M13() {} public void M14() {} public void M15() {} public void M16() {} public void M17() {}
                public void M18() {} public void M19() {} public void M20() {} public void M21() {} public void M22() {} public void M23() {}
                public void M24() {} public void M25() {} public void M26() {} public void M27() {} public void M28() {} public void M29() {}
            }
            public sealed class Fluent
            {
                public Fluent() { }
                public Fluent Chain(Action a) => this;
                public void F00() {} public void F01() {} public void F02() {} public void F03() {} public void F04() {}
                public void F05() {} public void F06() {} public void F07() {} public void F08() {} public void F09() {}
            }

            public static class Api
            {
                public static void Run(Action action) => action();
                public static int Add(int a, Bag bag) => a;
                public static int Only(int value, string text) => value;
                public static void Make(out Action action) { action = null; }
                public static string Name(Func<int> f) => "";
                public static IEnumerable<T> Each<T>(IEnumerable<T> items, Func<T, bool> keep) { foreach (var i in items) if (keep(i)) yield return i; }
                public static void Pair(Handler a, Renderer b, Action done) { }
                public static void Fill(Bag bag, Action done) { }
                public static void Sinks(ISink sink, Action done) { }
                public static void Hand(Runner runner, Action action) => runner.Go(action);
                public static void HandFixed(FixedRunner runner, Action action) => runner.Go(action);
                public static bool Match(Handler handler, Action action) => handler.Equals(action);
                public static void Sources(string text, Box box, Opaque opaque, Shape shape, Action done) { }
                public static void Map(Func<int, string, bool> f) { }
                public static void Returns(Action a, Func<Task> b, Func<Task<int>> c, Func<ValueTask<string>> d, Func<Task<Bag>> e) { }
                public static void Objects(Func<string> d, Func<object> e, Func<Box> f, Func<ISink> g) { }
                public static T Build<T>(Func<T> make) where T : IMessage<T> => make();
                public static void Hard(Func<Hidden> make) { }
                public static void Exchange(ref Bag bag, out Bag result, ref Action swap, in Action input, out Action output, Action done) { result = bag; output = done; }
                public static void Take(Hidden hidden, Action done) { }
                public static void Nest(Wrapper wrapper, Action done) { }
                public static void Note(System.Enum value, Action done) { }
                public static void Pairs(Func<Pair> make) { }
                public static void PairsOut(PairOut make) { }
                public static void Slots(Func<Slot> make) { }
                public static Holder Wrap(Box box) => new Holder(box.Make);
                public static async Task<Holder> RunAsync(Func<int> f) { await Task.Yield(); return new Holder(f); }
                public static Task<Task<Box>> BoxTwiceAsync(Func<int> make) => Task.FromResult(Task.FromResult(new Box(make)));
                public static void Valued<T>(T value, Action done) where T : struct { }
                public static void Unmanaged<T>(T value, Action done) where T : unmanaged { }
                public static void NoOne<T>(T value, Action done) where T : struct, ISink { }
                public static void GuardedArg<T>(T value, Action done) where T : Guarded { }
                public static void BagArg<T>(T value, Action done) where T : Bag { }
                public static void Sink<T>(T value, Action done) where T : ISink { }
                public static void Scan(ReadOnlySpan<int> span, Action done) { }
                public static unsafe void Point(int* p, Action done) { }
            }
        }
        """;

    private static readonly Lazy<LibraryCompilationResult> Library = new(() => CompileLibrary(LIBRARY));

    // ---- member kinds and candidates ----

    [Fact]
    public void A_property_id_is_accessor()
    {
        var synthesis = Synthesize("P:Lib.ISink.Count");

        Assert.Null(synthesis.Driver);
        Assert.Equal(GenerationReasons.ACCESSOR, synthesis.Reason);
        Assert.Contains("P:Lib.ISink.Count", synthesis.Detail);
        Assert.Contains("M:Lib.ISink.get_Count", synthesis.Detail);
    }

    [Fact]
    public void An_event_id_is_accessor()
    {
        var synthesis = Synthesize("E:Lib.ISink.Changed");

        Assert.Equal(GenerationReasons.ACCESSOR, synthesis.Reason);
        Assert.Contains("E:Lib.ISink.Changed", synthesis.Detail);
        Assert.Contains("M:Lib.ISink.add_Changed(System.EventHandler)", synthesis.Detail);
        Assert.Contains("M:Lib.ISink.remove_Changed(System.EventHandler)", synthesis.Detail);
    }

    [Fact]
    public void An_accessor_named_by_a_method_id_is_synthesized()
    {
        var synthesis = Synthesize("M:Lib.Renderer.get_Width");

        Assert.True(synthesis.Driver is not null, $"{synthesis.Reason}: {synthesis.Detail}");
        Assert.Contains("var r = Recv_Call.Width;", synthesis.Driver.Source);
    }

    [Fact]
    public void A_member_with_only_immutable_values_is_not_a_candidate()
    {
        Assert.Equal(GenerationReasons.NOT_A_CANDIDATE, Synthesize("M:Lib.Api.Only(System.Int32,System.String)").Reason);
        // An out delegate hands the member nothing.
        Assert.Equal(GenerationReasons.NOT_A_CANDIDATE, Synthesize("M:Lib.Api.Make(System.Action@)").Reason);
    }

    [Fact]
    public void A_member_whose_own_body_was_made_extern_is_body_does_not_compile_after_the_candidate_check()
    {
        var library = Library.Value.Compilation!;
        const string RUN = "M:Lib.Api.Run(System.Action)";
        const string ADD = "M:Lib.Api.Add(System.Int32,Lib.Bag)";
        var rewritten = new HashSet<string>([RUN, ADD], StringComparer.Ordinal);

        var run = DriverSynthesizer.Synthesize(library, DriverSynthesizer.FindMember(library, RUN)!, rewritten, CancellationToken.None);
        var add = DriverSynthesizer.Synthesize(library, DriverSynthesizer.FindMember(library, ADD)!, rewritten, CancellationToken.None);

        Assert.Equal(GenerationReasons.BODY_DOES_NOT_COMPILE, run.Reason);
        Assert.Equal(GenerationReasons.BODY_DOES_NOT_COMPILE, add.Reason);
    }

    // ---- setup and the call ----

    [Fact]
    public void Setup_builds_the_receiver_the_arguments_with_their_contents_and_the_intermediates()
    {
        var driver = Drive("M:Lib.Workshop.Use(System.Collections.Generic.IEnumerable{System.Int32},Lib.Outer,System.Action)");
        var setup = Body(driver, DriverSynthesizer.SETUP);

        Assert.Contains("Recv_Call = new Sub_Recv();", setup);
        Assert.Contains("Arg_numbers_Call = new global::System.Collections.Generic.List<global::System.Int32> { default(global::System.Int32), default(global::System.Int32) };", setup);
        Assert.Contains("Keep.K0 = new global::Lib.Middle();", setup);
        Assert.Contains("Arg_outer_Call = new global::Lib.Outer(Keep.K0, L_outer_0_Call());", setup);
        Assert.Contains("((global::Lib.Middle)((global::Lib.Outer)Arg_outer_Call).Middle).Seed = new Seed_", setup);
        Assert.True(setup.IndexOf("Keep.K0 =", StringComparison.Ordinal) < setup.IndexOf("Arg_outer_Call =", StringComparison.Ordinal));
        Assert.True(setup.IndexOf("Arg_outer_Call =", StringComparison.Ordinal) < setup.IndexOf(".Seed = new Seed_", StringComparison.Ordinal));
        var outer = Assert.Single(driver.Parameters, parameter => parameter.Name == "outer");
        Assert.Equal(ParameterKind.RecipeValue, outer.Kind);
        Assert.Equal("Arg_outer_Call", outer.OwnFields[DriverSynthesizer.CALL_VARIANT]);
    }

    [Fact]
    public void V_Call_only_reads_its_fields_creates_its_probes_and_calls()
    {
        foreach (var id in new[]
                 {
                     "M:Lib.Workshop.Use(System.Collections.Generic.IEnumerable{System.Int32},Lib.Outer,System.Action)",
                     "M:Lib.Api.Wrap(Lib.Box)",
                     "M:Lib.Api.RunAsync(System.Func{System.Int32})"
                 })
        {
            var driver = Drive(id);
            var operations = Operations(driver, DriverSynthesizer.CALL).ToArray();

            Assert.DoesNotContain(operations, operation => operation is IObjectCreationOperation or IArrayCreationOperation or IAnonymousFunctionOperation);
            Assert.All(operations.OfType<IInvocationOperation>(), invocation =>
                Assert.True(invocation.TargetMethod.OriginalDefinition.GetDocumentationCommentId() == driver.Member.GetDocumentationCommentId() ||
                            invocation.TargetMethod.Name.StartsWith("L_", StringComparison.Ordinal) && invocation.TargetMethod.Name.EndsWith("_Call", StringComparison.Ordinal),
                            $"{id}: V_Call calls {invocation.TargetMethod}"));
            Assert.All(operations.OfType<IFieldReferenceOperation>(), field =>
                Assert.True(field.Field.Name.EndsWith("_Call", StringComparison.Ordinal) || field.Field is { Name: "R", ContainingType.Name: DriverSynthesizer.KEEP_TYPE },
                            $"{id}: V_Call touches {field.Field}"));
            Assert.All(operations.OfType<ISimpleAssignmentOperation>(), assignment =>
                Assert.True(assignment.Target is IFieldReferenceOperation { Field.Name: "R" }, $"{id}: V_Call assigns {assignment.Target.Syntax}"));
        }
    }

    [Fact]
    public void Each_variant_has_its_own_receiver_and_argument_values()
    {
        var driver = Drive("M:Lib.Workshop.Use(System.Collections.Generic.IEnumerable{System.Int32},Lib.Outer,System.Action)");
        var setup = Body(driver, DriverSynthesizer.SETUP);

        Assert.Equal([DriverSynthesizer.SETUP, DriverSynthesizer.CALL], driver.Actions);
        // The receiver's members: Use itself, Close, and object's three.
        Assert.Equal(["T0", "T1", "T2", "T3", "T4"], driver.Triggers.Select(trigger => trigger.Action));
        foreach (var variant in new[] { DriverSynthesizer.CALL_VARIANT, "T0" })
        {
            Assert.Contains($"Recv_{variant} = new Sub_Recv();", setup);
            Assert.Contains($"Arg_numbers_{variant} = new global::System.Collections.Generic.List<", setup);
            Assert.Contains($"Arg_outer_{variant} = new global::Lib.Outer(Keep.K", setup);
            Assert.Contains($"L_outer_0_{variant}()", setup);
            Assert.Contains($"Recv_{variant}.Use(Arg_numbers_{variant}, Arg_outer_{variant}, L_done_0_{variant}())", Body(driver, variant == "T0" ? "T0" : DriverSynthesizer.CALL));
        }

        Assert.Contains("Keep.K0 = new global::Lib.Middle();", setup);
        Assert.Contains("Keep.K1 = new global::Lib.Middle();", setup);
        Assert.Contains("Recv_T1.Close();", Body(driver, "T1"));
    }

    // ---- probe classes and subclasses ----

    [Fact]
    public void Two_unrelated_abstract_class_parameters_each_get_their_own_probe_class()
    {
        var driver = Drive("M:Lib.Api.Pair(Lib.Handler,Lib.Renderer,System.Action)");

        var a = Type(driver, "Probe_a");
        var b = Type(driver, "Probe_b");
        Assert.Equal("Lib.Handler", a.BaseType!.ToDisplayString());
        Assert.Equal("Lib.Renderer", b.BaseType!.ToDisplayString());
        Assert.True(a.IsSealed && b.IsSealed);
        Assert.Contains("Arg_a_Call = new Probe_a() { Ref0 = new Probe_a() };", Body(driver, DriverSynthesizer.SETUP));
        Assert.Contains("Arg_b_Call = new Probe_b() { Ref0 = new Probe_b() };", Body(driver, DriverSynthesizer.SETUP));
    }

    [Fact]
    public void A_probe_class_has_two_reference_fields_and_an_int_field_and_witnesses_every_object_and_base_member()
    {
        var probe = Type(Drive("M:Lib.Api.Pair(Lib.Handler,Lib.Renderer,System.Action)"), "Probe_a");

        Assert.Equal(["Int0:int", "Ref0:object", "Ref1:object"],
                     probe.GetMembers().OfType<IFieldSymbol>().Select(field => $"{field.Name}:{field.Type.ToDisplayString()}").Order(StringComparer.Ordinal));
        AssertWitnessOverrides(probe, "Equals", "GetHashCode", "Handle", "Reset", "ToString");
    }

    [Fact]
    public void A_probe_class_implements_every_interface_member_default_ones_included_as_witnesses()
    {
        var driver = Drive("M:Lib.Api.Sinks(Lib.ISink,System.Action)");
        var probe = Type(driver, "Probe_sink");
        var sink = driver.Compilation.GetTypeByMetadataName("Lib.ISink")!;

        Assert.Contains(sink, probe.Interfaces, SymbolEqualityComparer.Default);
        AssertWitnessOverrides(probe, "Equals", "GetHashCode", "ToString");
        foreach (var member in sink.GetMembers().Where(member => member is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol))
        {
            var implementation = probe.FindImplementationForInterfaceMember(member);
            Assert.True(implementation is not null && !implementation.IsExtern && SymbolEqualityComparer.Default.Equals(implementation.ContainingType, probe),
                        $"{member} is implemented by {implementation}");
        }
    }

    [Fact]
    public void A_delegate_handed_to_an_overridable_member_of_a_probe_object_has_an_unknown_execution_fate_through_the_witness()
    {
        // Runner.Go would run the delegate during the call; the probe class witnesses receiving it, which is still user code that
        // may keep and run it anywhere.
        var open = Analyze(Drive("M:Lib.Api.Hand(Lib.Runner,System.Action)"));
        var known = Analyze(Drive("M:Lib.Api.HandFixed(Lib.FixedRunner,System.Action)"));

        Assert.False(open.Stopped);
        Assert.False(known.Stopped);
        Assert.Equal(FateClassifier.UNKNOWN_EXECUTION,
                     FateClassifier.Classify(Drive("M:Lib.Api.Hand(Lib.Runner,System.Action)"), open).Classified["action"].Fate);
        Assert.Equal([ExecutionKind.Root], FiredIn(known, "P_action_0_Call"));

        var equalsDriver = Drive("M:Lib.Api.Match(Lib.Handler,System.Action)");
        Assert.Equal(FateClassifier.UNKNOWN_EXECUTION,
                     FateClassifier.Classify(equalsDriver, Analyze(equalsDriver)).Classified["action"].Fate);
    }

    [Fact]
    public void A_non_sealed_concrete_parameter_gets_its_subclass_with_witness_overrides()
    {
        var driver = Drive("M:Lib.Api.Fill(Lib.Bag,System.Action)");
        var subclass = Type(driver, "Sub_bag");

        Assert.Equal("Lib.Bag", subclass.BaseType!.ToDisplayString());
        AssertWitnessOverrides(subclass, "Add", "Equals", "GetHashCode", "ToString");
        Assert.Contains("Arg_bag_Call = new Sub_bag();", Body(driver, DriverSynthesizer.SETUP));
    }

    [Fact]
    public void A_base_class_whose_only_accessible_constructor_takes_arguments_is_called_with_the_recipes_values()
    {
        var driver = Drive("M:Lib.Api.Pair(Lib.Handler,Lib.Renderer,System.Action)");

        Assert.Contains("public Probe_b() : base(\"s\") { }", driver.Source);
        Assert.Contains("public override global::System.Int32 Width { get {", driver.Source);
    }

    [Fact]
    public void A_non_sealed_receiver_gets_its_subclass_with_the_member_and_what_it_overrides_left_alone()
    {
        var driver = Drive("M:Lib.Special.Run(System.Action)");
        var receiver = Type(driver, "Sub_Recv");

        Assert.Equal("Lib.Special", receiver.BaseType!.ToDisplayString());
        AssertWitnessOverrides(receiver, "Equals", "GetHashCode", "Other", "ToString");
        Assert.Contains("Recv_Call = new Sub_Recv();", Body(driver, DriverSynthesizer.SETUP));
        Assert.Contains("Recv_Call.Run(L_action_0_Call());", Body(driver, DriverSynthesizer.CALL));
    }

    [Fact]
    public void A_receiver_type_without_an_accessible_constructor_takes_the_recipes_value()
    {
        var driver = Drive("M:Lib.Locked.Run(System.Action)");

        Assert.Contains("Recv_Call = global::Lib.Locked.Open();", Body(driver, DriverSynthesizer.SETUP));
        Assert.Null(driver.Compilation.GetTypeByMetadataName("Sub_Recv"));
    }

    // ---- type arguments ----

    [Fact]
    public void A_type_parameter_gets_its_probe_class_as_argument()
    {
        var driver = Drive("M:Lib.Api.Each``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})");

        Assert.Contains("global::Lib.Api.Each<Probe_T>(Arg_items_Call, L_keep_0_Call())", Body(driver, DriverSynthesizer.CALL));
        Assert.Contains("Arg_items_Call = new global::System.Collections.Generic.List<Probe_T> { new Probe_T() { Ref0 = new Probe_T() }, new Probe_T() { Ref0 = new Probe_T() } };",
                        Body(driver, DriverSynthesizer.SETUP));
        AssertWitnessOverrides(Type(driver, "Probe_T"), "Equals", "GetHashCode", "ToString");
    }

    [Fact]
    public void A_concrete_class_constraint_is_met_by_a_probe_class_deriving_from_it_with_witness_overrides()
    {
        var driver = Drive("M:Lib.Api.BagArg``1(``0,System.Action)");
        var probe = Type(driver, "Probe_T");

        Assert.Equal("Lib.Bag", probe.BaseType!.ToDisplayString());
        AssertWitnessOverrides(probe, "Add", "Equals", "GetHashCode", "ToString");
        Assert.Contains("global::Lib.Api.BagArg<Probe_T>(Arg_value_Call, L_done_0_Call())", Body(driver, DriverSynthesizer.CALL));
    }

    [Fact]
    public void Struct_and_unmanaged_constraints_take_the_recipes_int()
    {
        Assert.Contains("global::Lib.Api.Valued<global::System.Int32>(Arg_value_Call", Body(Drive("M:Lib.Api.Valued``1(``0,System.Action)"), DriverSynthesizer.CALL));
        Assert.Contains("global::Lib.Api.Unmanaged<global::System.Int32>(Arg_value_Call", Body(Drive("M:Lib.Api.Unmanaged``1(``0,System.Action)"), DriverSynthesizer.CALL));
    }

    [Fact]
    public void Constraints_no_one_type_meets_are_driver_not_synthesized()
    {
        var synthesis = Synthesize("M:Lib.Api.NoOne``1(``0,System.Action)");

        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, synthesis.Reason);
        Assert.Contains("no type argument meets the constraints of T", synthesis.Detail);
    }

    [Fact]
    public void A_base_class_constraint_without_an_accessible_constructor_takes_the_recipes_choice()
    {
        var driver = Drive("M:Lib.Api.GuardedArg``1(``0,System.Action)");

        Assert.Contains("global::Lib.Api.GuardedArg<global::Lib.Guarded>(Arg_value_Call", Body(driver, DriverSynthesizer.CALL));
        Assert.Contains("Arg_value_Call = global::Lib.Guarded.Create();", Body(driver, DriverSynthesizer.SETUP));
        Assert.Null(driver.Compilation.GetTypeByMetadataName("Probe_T"));
    }

    [Fact]
    public void An_interface_constraint_is_met_by_a_probe_class_with_witness_members()
    {
        var driver = Drive("M:Lib.Api.Sink``1(``0,System.Action)");
        var probe = Type(driver, "Probe_T");

        Assert.Equal(["Lib.ISink"], probe.Interfaces.Select(@interface => @interface.ToDisplayString()));
        Assert.All(probe.GetMembers().OfType<IMethodSymbol>().Where(method => method.MethodKind is MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation),
                   method => Assert.False(method.IsExtern, $"{method} has no body"));
    }

    [Fact]
    public void A_self_referential_interface_constraint_is_met_by_a_probe_class_of_itself()
    {
        var driver = Drive("M:Lib.Api.Build``1(System.Func{``0})");
        var probe = Type(driver, "Probe_T");

        Assert.Equal(["Lib.IMessage<Probe_T>"], probe.Interfaces.Select(@interface => @interface.ToDisplayString()));
        Assert.False(probe.GetMembers().OfType<IMethodSymbol>().Single(method => method.Name.EndsWith("Clone", StringComparison.Ordinal)).IsExtern);
        Assert.Contains("return new Probe_T() { Ref0 = new Probe_T() };", driver.Source);
    }

    // ---- values ----

    [Fact]
    public void Value_sources_follow_the_recipes_order()
    {
        var driver = Drive("M:Lib.Api.Sources(System.String,Lib.Box,Lib.Opaque,Lib.Shape,System.Action)");
        var setup = Body(driver, DriverSynthesizer.SETUP);

        Assert.Contains("Arg_text_Call = \"s\";", setup);
        Assert.Contains("Arg_box_Call = new global::Lib.Box(L_box_0_Call());", setup);
        Assert.Contains("Arg_opaque_Call = global::Lib.Opaque.Create();", setup);
        Assert.Contains("Arg_shape_Call = new global::Lib.Circle();", setup);
        Assert.Contains("global::Lib.Api.Sources(Arg_text_Call, Arg_box_Call, Arg_opaque_Call, Arg_shape_Call, L_done_0_Call())", Body(driver, DriverSynthesizer.CALL));
    }

    [Fact]
    public void The_recipe_takes_the_constructor_with_a_delegate_when_placing_probes_and_the_fewest_parameters_otherwise()
    {
        Assert.Contains("Arg_box_Call = new global::Lib.Box(L_box_0_Call());",
                        Body(Drive("M:Lib.Api.Sources(System.String,Lib.Box,Lib.Opaque,Lib.Shape,System.Action)"), DriverSynthesizer.SETUP));
        Assert.Contains("return new global::Lib.Box();",
                        Drive("M:Lib.Api.Objects(System.Func{System.String},System.Func{System.Object},System.Func{Lib.Box},System.Func{Lib.ISink})").Source);
    }

    [Fact]
    public void A_probe_lambda_is_made_by_a_factory_per_probe_and_variant_and_stores_its_parameters()
    {
        var driver = Drive("M:Lib.Api.Map(System.Func{System.Int32,System.String,System.Boolean})");
        var factory = driver.Compilation.GetTypeByMetadataName(DriverSynthesizer.DRIVER_TYPE)!.GetMembers("L_f_0_Call").OfType<IMethodSymbol>().Single();
        var body = ((MethodDeclarationSyntax)factory.DeclaringSyntaxReferences.Single().GetSyntax()).ExpressionBody!.ToString();

        Assert.Contains("P_f_0_Call = 1;", body);
        Assert.Contains("In_f_0_Call = a", body);
        Assert.Contains("In_f_1_Call = a", body);
        Assert.True(body.IndexOf("P_f_0_Call = 1;", StringComparison.Ordinal) < body.IndexOf("In_f_0_Call", StringComparison.Ordinal));
        var probe = Assert.Single(Assert.Single(driver.Parameters).Probes);
        Assert.Equal(("f", DriverSynthesizer.CALL_VARIANT, 0, "L_f_0_Call", "P_f_0_Call"), (probe.Parameter, probe.Variant, probe.Index, probe.Factory, probe.FiredField));
        Assert.Equal(["In_f_0_Call", "In_f_1_Call"], probe.InputFields);
        Assert.Same(factory, DocumentationCommentId.GetFirstSymbolForDeclarationId(probe.FactoryId, driver.Compilation));
    }

    [Fact]
    public void Probe_lambdas_return_completed_tasks_of_values_made_in_the_lambda()
    {
        var source = Drive("M:Lib.Api.Returns(System.Action,System.Func{System.Threading.Tasks.Task},System.Func{System.Threading.Tasks.Task{System.Int32}}," +
                           "System.Func{System.Threading.Tasks.ValueTask{System.String}},System.Func{System.Threading.Tasks.Task{Lib.Bag}})").Source;

        Assert.Contains("(() => { P_a_0_Call = 1; })", source);
        Assert.Contains("(() => { P_b_0_Call = 1; return global::System.Threading.Tasks.Task.CompletedTask; })", source);
        Assert.Contains("return global::System.Threading.Tasks.Task.FromResult<global::System.Int32>(default(global::System.Int32));", source);
        Assert.Contains("return new global::System.Threading.Tasks.ValueTask<global::System.String>(\"s\");", source);
        Assert.Contains("return global::System.Threading.Tasks.Task.FromResult<global::Lib.Bag>(new global::Lib.Bag());", source);
    }

    [Fact]
    public void Probe_lambdas_return_a_string_a_marker_and_a_library_object_made_in_the_lambda()
    {
        var driver = Drive("M:Lib.Api.Objects(System.Func{System.String},System.Func{System.Object},System.Func{Lib.Box},System.Func{Lib.ISink})");

        Assert.Contains("(() => { P_d_0_Call = 1; return \"s\"; })", driver.Source);
        Assert.Contains("(() => { P_e_0_Call = 1; return new R_e(); })", driver.Source);
        Assert.Contains("(() => { P_f_0_Call = 1; return new global::Lib.Box(); })", driver.Source);
        Assert.Contains("(() => { P_g_0_Call = 1; return new R_g(); })", driver.Source);
        Assert.True(Type(driver, "R_e").IsSealed);
        Assert.Equal(["Lib.ISink"], Type(driver, "R_g").Interfaces.Select(@interface => @interface.ToDisplayString()));
        Assert.All(Type(driver, "R_g").GetMembers().OfType<IMethodSymbol>().Where(method => method.MethodKind != MethodKind.Constructor),
                   method => Assert.False(method.IsExtern, $"{method} has no body"));
    }

    [Fact]
    public void A_return_type_the_lambda_cannot_make_is_driver_not_synthesized()
    {
        var synthesis = Synthesize("M:Lib.Api.Hard(System.Func{Lib.Hidden})");

        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, synthesis.Reason);
        Assert.Contains("Lib.Hidden", synthesis.Detail);
    }

    [Fact]
    public void An_in_delegate_parameter_is_handed_its_probe_as_a_value()
    {
        var driver = Drive(EXCHANGE);
        var input = Assert.Single(driver.Parameters, parameter => parameter.Name == "input");

        Assert.Contains(", L_input_0_Call(), ", Body(driver, DriverSynthesizer.CALL));
        Assert.Equal(RefKind.In, input.RefKind);
        Assert.Empty(input.OwnFields);
        Assert.Equal("L_input_0_Call", Assert.Single(input.Probes).Factory);
    }

    [Fact]
    public void An_out_delegate_parameter_gets_only_its_field_and_is_not_classified()
    {
        var driver = Drive(EXCHANGE);

        Assert.Contains("out Out_output_Call", Body(driver, DriverSynthesizer.CALL));
        Assert.DoesNotContain(driver.Parameters, parameter => parameter.Name == "output");
        Assert.DoesNotContain("L_output_", driver.Source);
        Assert.DoesNotContain("Out_output_Call =", Body(driver, DriverSynthesizer.SETUP));
    }

    [Fact]
    public void A_ref_delegate_parameters_field_is_set_to_its_probe_by_the_action_right_before_the_call()
    {
        var driver = Drive(EXCHANGE);
        var lines = Lines(driver, DriverSynthesizer.CALL);
        var swap = Assert.Single(driver.Parameters, parameter => parameter.Name == "swap");

        Assert.Equal("Out_swap_Call = L_swap_0_Call();", lines[0]);
        Assert.StartsWith("global::Lib.Api.Exchange(ref Out_bag_Call, out Out_result_Call, ref Out_swap_Call, ", lines[1]);
        Assert.Equal("Out_swap_Call", swap.OwnFields[DriverSynthesizer.CALL_VARIANT]);
        Assert.DoesNotContain("Out_swap_Call =", Body(driver, DriverSynthesizer.SETUP));
    }

    [Fact]
    public void Out_and_ref_holding_arguments_go_through_out_fields()
    {
        var driver = Drive(EXCHANGE);
        var setup = Body(driver, DriverSynthesizer.SETUP);

        Assert.Contains("Out_bag_Call = new Sub_bag();", setup);
        Assert.DoesNotContain("Out_result_Call =", setup);
        Assert.NotNull(driver.Compilation.GetTypeByMetadataName(DriverSynthesizer.DRIVER_TYPE)!.GetMembers("Out_result_Call").OfType<IFieldSymbol>().SingleOrDefault());
    }

    [Fact]
    public void A_holding_parameter_only_a_default_could_fill_is_driver_not_synthesized()
    {
        var synthesis = Synthesize("M:Lib.Api.Take(Lib.Hidden,System.Action)");

        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, synthesis.Reason);
        Assert.Contains("only a default could fill the Lib.Hidden of hidden", synthesis.Detail);
    }

    [Fact]
    public void A_holding_constructor_argument_one_level_inside_a_recipe_value_is_driver_not_synthesized()
    {
        var synthesis = Synthesize("M:Lib.Api.Nest(Lib.Wrapper,System.Action)");

        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, synthesis.Reason);
        Assert.Contains("only a default could fill the Lib.Inner of wrapper", synthesis.Detail);
    }

    [Fact]
    public void A_non_holding_value_only_a_default_could_fill_is_named_in_defaulted_values()
    {
        var driver = Drive("M:Lib.Api.Note(System.Enum,System.Action)");

        Assert.Equal(["value"], driver.DefaultedValues);
        Assert.Contains("Arg_value_Call = default(global::System.Enum);", Body(driver, DriverSynthesizer.SETUP));
        Assert.Empty(Drive("M:Lib.Api.Run(System.Action)").DefaultedValues);
    }

    [Fact]
    public void A_struct_with_references_a_lambda_returns_is_never_a_default()
    {
        // A struct holding a reference holds as a class does: without a constructor or factory the recipe could call, a lambda's
        // return or out value would be a made-up default, so the member is not synthesized; with one, it is built, never defaulted.
        var returned = Synthesize("M:Lib.Api.Pairs(System.Func{Lib.Pair})");
        var assigned = Synthesize("M:Lib.Api.PairsOut(Lib.PairOut)");
        var built = Drive("M:Lib.Api.Slots(System.Func{Lib.Slot})");

        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, returned.Reason);
        Assert.Contains("only a default could fill the Lib.Pair", returned.Detail);
        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, assigned.Reason);
        Assert.Contains("only a default could fill the Lib.Pair", assigned.Detail);
        Assert.Empty(built.DefaultedValues);
        Assert.Contains("return new global::Lib.Slot(", built.Source);
    }

    [Fact]
    public void A_carried_probe_is_built_by_setup_once_per_variant_with_that_variants_probe()
    {
        var driver = Drive("M:Lib.Api.Wrap(Lib.Box)");
        var setup = Body(driver, DriverSynthesizer.SETUP);
        var box = Assert.Single(driver.Parameters);

        Assert.Contains("Arg_box_Call = new global::Lib.Box(L_box_0_Call());", setup);
        Assert.Contains("Arg_box_T0 = new global::Lib.Box(L_box_0_T0());", setup);
        Assert.Equal(ParameterKind.RecipeValue, box.Kind);
        Assert.Equal(["Call:0:L_box_0_Call", .. Enumerable.Range(0, 5).Select(index => $"T{index}:0:L_box_0_T{index}")],
                     box.Probes.Select(probe => $"{probe.Variant}:{probe.Index}:{probe.Factory}"));
        Assert.Equal("Arg_box_T0", box.OwnFields["T0"]);
        // Every member a caller could invoke on the returned Holder: its own methods, then object's overridable ones.
        Assert.Equal([new DriverTrigger("T0", "M:Lib.Holder.Run", FateClassifier.RESULT), new DriverTrigger("T1", "M:Lib.Holder.RunAsync", FateClassifier.RESULT)],
                     driver.Triggers.Take(2));
        Assert.Equal(["M:System.Object.Equals(System.Object)", "M:System.Object.GetHashCode", "M:System.Object.ToString"],
                     driver.Triggers.Skip(2).Select(trigger => trigger.Member).Order(StringComparer.Ordinal));
        Assert.True(driver.Covers(FateClassifier.RESULT));
        Assert.False(driver.Covers(FateClassifier.THIS));
    }

    // ---- actions ----

    [Fact]
    public void V_Call_awaits_a_task_result_and_keeps_what_it_completes_with()
    {
        var driver = Drive("M:Lib.Api.RunAsync(System.Func{System.Int32})");
        var call = driver.Compilation.GetTypeByMetadataName(DriverSynthesizer.DRIVER_TYPE)!.GetMembers(DriverSynthesizer.CALL).OfType<IMethodSymbol>().Single();

        Assert.True(call.IsAsync);
        Assert.Equal(["var r = await global::Lib.Api.RunAsync(L_f_0_Call());", "Keep.R = r;"], Lines(driver, DriverSynthesizer.CALL));
        Assert.Equal("Lib.Holder", driver.Compilation.GetTypeByMetadataName(DriverSynthesizer.KEEP_TYPE)!.GetMembers("R").OfType<IFieldSymbol>().Single().Type.ToDisplayString());
        Assert.Contains("await r.RunAsync();", driver.Source);
    }

    [Fact]
    public void V_Call_of_a_task_of_a_task_awaits_twice_and_keeps_the_innermost_completion_value()
    {
        var driver = Drive("M:Lib.Api.BoxTwiceAsync(System.Func{System.Int32})");

        Assert.Equal(["var r = await await global::Lib.Api.BoxTwiceAsync(L_make_0_Call());", "Keep.R = r;"], Lines(driver, DriverSynthesizer.CALL));
        Assert.Equal("Lib.Box", driver.Compilation.GetTypeByMetadataName(DriverSynthesizer.KEEP_TYPE)!.GetMembers("R").OfType<IFieldSymbol>().Single().Type.ToDisplayString());
    }

    [Fact]
    public void V_Enum_exists_only_for_an_enumerable_result()
    {
        var each = Drive("M:Lib.Api.Each``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})");

        Assert.True(each.Enumerates);
        Assert.Contains(DriverSynthesizer.ENUMERATE, each.Actions);
        Assert.Equal(["var r = global::Lib.Api.Each<Probe_T>(Arg_items_Enum, L_keep_0_Enum());", "foreach (var e in r) { }"], Lines(each, DriverSynthesizer.ENUMERATE));
        foreach (var id in new[] { "M:Lib.Api.Run(System.Action)", "M:Lib.Api.Name(System.Func{System.Int32})" })
        {
            var driver = Drive(id);
            Assert.False(driver.Enumerates);
            Assert.DoesNotContain(DriverSynthesizer.ENUMERATE, driver.Actions);
        }
    }

    [Fact]
    public void At_most_24_triggers_each_with_its_own_call()
    {
        var driver = Drive("M:Lib.Many.#ctor(System.Action)");

        Assert.Equal(24, driver.Triggers.Count);
        Assert.Equal(Enumerable.Range(0, 24).Select(index => "T" + index), driver.Triggers.Select(trigger => trigger.Action));
        Assert.Equal(Enumerable.Range(0, 24).Select(index => $"M:Lib.Many.M{index:00}"), driver.Triggers.Select(trigger => trigger.Member));
        for (var index = 0; index < 24; index++)
            Assert.Equal([$"var r = new global::Lib.Many(L_a_0_T{index}());", $"r.M{index:00}();"], Lines(driver, "T" + index));
        Assert.Equal(25, Assert.Single(driver.Parameters).Probes.Count);
    }

    [Fact]
    public void The_result_and_the_receiver_holders_share_the_24_triggers()
    {
        // Chain's result and its receiver are both a Fluent: each holder alone fits in 24 triggers, the two together do not. The
        // result's take the first variants, the receiver's get the rest, and the receiver's members past T23 leave it uncovered.
        var driver = Drive("M:Lib.Fluent.Chain(System.Action)");
        var result = driver.Triggers.Where(trigger => trigger.Holder == FateClassifier.RESULT).ToArray();
        var receiver = driver.Triggers.Where(trigger => trigger.Holder == FateClassifier.THIS).ToArray();

        Assert.Equal(Enumerable.Range(0, 24).Select(index => "T" + index), driver.Triggers.Select(trigger => trigger.Action));
        Assert.DoesNotContain("T24(", driver.Source);
        Assert.Equal(driver.Triggers.Count, Assert.Single(driver.Parameters).Probes.Count - 1);
        Assert.InRange(result.Length, 1, 23);
        Assert.Equal(24 - result.Length, receiver.Length);
        Assert.Equal(result, driver.Triggers.Take(result.Length));
        Assert.True(driver.Covers(FateClassifier.RESULT));
        Assert.Empty(driver.Uncovered[FateClassifier.RESULT]);
        Assert.False(driver.Covers(FateClassifier.THIS));
        Assert.Equal(result.Length - receiver.Length, driver.Uncovered[FateClassifier.THIS].Count);
        Assert.All(driver.Uncovered[FateClassifier.THIS], why => Assert.Contains("past the driver's 24 triggers", why));
    }

    [Fact]
    public void An_operator_is_called_with_its_operator_syntax()
    {
        var driver = Drive("M:Lib.Money.op_Addition(Lib.Money,System.Func{System.Int32})");

        Assert.Equal(["var r = (Arg_a_Call + L_b_0_Call());", "Keep.R = r;"], Lines(driver, DriverSynthesizer.CALL));
    }

    [Fact]
    public void A_span_or_a_pointer_parameter_is_driver_not_synthesized()
    {
        var span = Synthesize("M:Lib.Api.Scan(System.ReadOnlySpan{System.Int32},System.Action)");
        var pointer = Synthesize("M:Lib.Api.Point(System.Int32*,System.Action)");

        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, span.Reason);
        Assert.Contains("ref-like or pointer", span.Detail);
        Assert.Equal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, pointer.Reason);
        Assert.Contains("ref-like or pointer", pointer.Detail);
    }

    // ---- roots ----

    [Fact]
    public void The_root_provider_discovers_every_action()
    {
        var driver = Drive("M:Lib.Many.#ctor(System.Action)");
        var provider = new DriverRootProvider();
        var context = new RootDiscoveryContext("scope:Driver", [Library.Value.Compilation!, driver.Compilation], ROOT,
                                               DiIndexBuilder.Build("scope:Driver", Array.Empty<Compilation>(), ROOT, CancellationToken.None), CancellationToken.None);

        var result = provider.Discover(context);

        Assert.Equal(RootDiscoveryStatus.Checked, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(driver.Actions.Select(action => $"model-driver:{DriverSynthesizer.ASSEMBLY}:M:{DriverSynthesizer.DRIVER_TYPE}.{action}").Order(StringComparer.Ordinal),
                     result.Roots.Select(root => root.StableRootId).Order(StringComparer.Ordinal));
        Assert.All(result.Roots, root =>
        {
            Assert.Equal(DriverRootProvider.PROVIDER_ID, root.ProviderId);
            Assert.Equal(ReceiverKind.None, root.InstanceBindings.Receiver);
            Assert.StartsWith($"body:{DriverSynthesizer.ASSEMBLY}:M:{DriverSynthesizer.DRIVER_TYPE}.", root.Entry.BodyKey);
        });
        Assert.DoesNotContain(result.Roots, root => root.Entry.BodyKey.Contains(".L_", StringComparison.Ordinal));
        // The triggers stay in the driver's source, but the fate run does not root them.
        Assert.Equal(24, driver.Triggers.Count);
        Assert.DoesNotContain(result.Roots, root => driver.Triggers.Any(trigger => root.StableRootId == DriverRootProvider.RootId(trigger.Action)));
        // The library alone has no driver, so no root.
        Assert.Empty(provider.Discover(context with { Compilations = [Library.Value.Compilation!] }).Roots);

        var run = Analyze(driver);
        Assert.Equal(driver.Actions.Count, run.RootsPerProvider[DriverRootProvider.PROVIDER_ID]);
        Assert.Empty(run.LoweringDiagnostics);
    }

    [Fact]
    public void A_driver_for_the_installed_Enumerable_Where_compiles()
    {
        var resolution = ImplementationAssemblies.ForThisProcess().Resolve("System.Linq", Environment.Version.Major.ToString(), null, null);
        Assert.True(resolution.Reason is null, resolution.Detail);
        var library = LibraryCompilation.Compile(resolution.Assembly!, CancellationToken.None);
        Assert.True(library.Reason is null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        const string WHERE = "M:System.Linq.Enumerable.Where``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})";

        var synthesis = DriverSynthesizer.Synthesize(library.Compilation!, DriverSynthesizer.FindMember(library.Compilation!, WHERE)!, library.ExternMembers,
                                                     CancellationToken.None);

        Assert.True(synthesis.Driver is not null, $"{synthesis.Reason}: {synthesis.Detail}");
        Assert.Empty(synthesis.Driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.True(synthesis.Driver.Enumerates);
        Assert.Contains("global::System.Linq.Enumerable.Where<Probe_TSource>(Arg_source_Call, L_predicate_0_Call())", synthesis.Driver.Source);
        var predicate = Assert.Single(synthesis.Driver.Parameters);
        Assert.Equal("predicate", predicate.Name);
        Assert.Contains(predicate.Probes, probe => probe.Variant == DriverSynthesizer.CALL_VARIANT);
        Assert.Contains(predicate.Probes, probe => probe.Variant == DriverSynthesizer.ENUMERATE_VARIANT);
    }

    // ---- helpers ----

    private const string EXCHANGE = "M:Lib.Api.Exchange(Lib.Bag@,Lib.Bag@,System.Action@,System.Action@,System.Action@,System.Action)";

    private static readonly string ROOT = Path.GetTempPath();

    private static LibraryCompilationResult CompileLibrary(string source)
    {
        var library = LibraryCompilation.CompileTrees("Fixture.Library",
                                                      [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), "Fixture.Library/Library.cs")],
                                                      EmittedAssemblies.RuntimeReferences, CancellationToken.None);
        Assert.True(library.Reason is null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        Assert.True(library.ExternBodies == 0, string.Join(", ", library.ExternMembers));
        return library;
    }

    private static DriverSynthesis Synthesize(string memberId)
    {
        var library = Library.Value.Compilation!;
        var member = DriverSynthesizer.FindMember(library, memberId);
        Assert.True(member is not null, $"{memberId} is not in the library");
        return DriverSynthesizer.Synthesize(library, member, Library.Value.ExternMembers, CancellationToken.None);
    }

    private static Driver Drive(string memberId)
    {
        var synthesis = Synthesize(memberId);
        Assert.True(synthesis.Driver is not null, $"{memberId}: {synthesis.Reason}: {synthesis.Detail}");
        Assert.Null(synthesis.Reason);
        return synthesis.Driver;
    }

    private static INamedTypeSymbol Type(Driver driver, string name)
    {
        var type = driver.Compilation.GetTypeByMetadataName(name);
        Assert.True(type is not null, $"the driver declares no {name}:\n{driver.Source}");
        return type;
    }

    private static MethodDeclarationSyntax Method(Driver driver, string name) =>
        driver.Compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.Text == name);

    private static string Body(Driver driver, string action) => Method(driver, action).Body!.ToString();

    private static string[] Lines(Driver driver, string action) =>
        Method(driver, action).Body!.Statements.Select(statement => statement.ToString()).ToArray();

    private static IEnumerable<IOperation> Operations(Driver driver, string action)
    {
        var method = Method(driver, action);
        return driver.Compilation.GetSemanticModel(method.SyntaxTree).GetOperation(method)!.Descendants();
    }

    /// <summary>Asserts that a driver class overrides exactly the named members, each with a witness body.</summary>
    /// <param name="type">The driver class.</param>
    /// <param name="names">The names of the members it must override.</param>
    private static void AssertWitnessOverrides(INamedTypeSymbol type, params string[] names)
    {
        var overrides = type.GetMembers().Where(member => member.IsOverride).ToArray();
        Assert.Equal(names.Order(StringComparer.Ordinal), overrides.Select(member => member.Name).Order(StringComparer.Ordinal));
        Assert.All(overrides, member => Assert.False(member.IsExtern, $"{member} has no body"));
    }

    /// <summary>The kinds of the executions in which a probe's fired field is written.</summary>
    /// <param name="run">The pipeline run.</param>
    /// <param name="field">The probe's fired field.</param>
    private static ExecutionKind[] FiredIn(ScopeRun run, string field) =>
        run.Collection!.Accesses.Where(access => access.Resource.Member.Name == field)
           .Select(access => run.Executions!.Execution(access.ExecutionId).Kind)
           .Distinct()
           .ToArray();

    /// <summary>Runs the scope pipeline over the library and a driver with the generator's root provider alone.</summary>
    /// <param name="driver">The driver.</param>
    private static ScopeRun Analyze(Driver driver)
    {
        Compilation[] compilations = [Library.Value.Compilation!, driver.Compilation];
        return ScopePipeline.Run("scope:Driver", compilations, compilations.Select(compilation => (compilation, (string?)null)).ToArray(), ROOT,
                                 new ProviderRegistry([new DriverRootProvider()]), LibraryModels.BuiltIn, AnalysisLimits.Default, new(), null,
                                 CancellationToken.None);
    }
}
