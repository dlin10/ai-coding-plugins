using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class InterfaceOverrideDispatchTests
{
    [Fact]
    public async Task Override_of_a_virtual_implementing_method_runs()
    {
        var result = await Run("""
            public interface I { void M(); }
            public class Base : I { public virtual void M() => Marks.Base++; }
            public sealed class Derived : Base { public override void M() => Marks.Derived++; }
            """, "I value = new Derived(); value.M();");

        Runs(result, "Derived.M", "Base.M");
    }

    [Fact]
    public async Task Override_of_an_abstract_implementing_method_runs_without_a_gap()
    {
        var result = await Run("""
            public interface I { void M(); }
            public abstract class Base : I { public abstract void M(); }
            public sealed class Derived : Base { public override void M() => Marks.Derived++; }
            """, "I value = new Derived(); value.M();");

        Runs(result, "Derived.M");
        Assert.Equal(0, result.Coverage[0].Skips[CoverageCounters.NO_RECEIVER_OBJECT]);
        Assert.Empty(result.Coverage[0].Gaps);
    }

    [Fact]
    public async Task Most_derived_override_of_a_two_level_chain_runs()
    {
        var result = await Run("""
            public interface I { void M(); }
            public class Base : I { public virtual void M() => Marks.Base++; }
            public class Middle : Base { public override void M() => Marks.Middle++; }
            public sealed class Derived : Middle { public override void M() => Marks.Derived++; }
            """, "I value = new Derived(); value.M();");

        Runs(result, "Derived.M", "Base.M", "Middle.M");
    }

    [Fact]
    public async Task Hiding_method_does_not_implement_the_interface()
    {
        var result = await Run("""
            public interface I { void M(); }
            public class Base : I { public void M() => Marks.Base++; }
            public sealed class Derived : Base { public new void M() => Marks.Derived++; }
            """, "I value = new Derived(); value.M();");

        Runs(result, "Base.M", "Derived.M");
    }

    [Fact]
    public async Task New_method_in_a_type_that_relists_the_interface_implements_it()
    {
        var result = await Run("""
            public interface I { void M(); }
            public class Base : I { public void M() => Marks.Base++; }
            public sealed class Derived : Base, I { public new void M() => Marks.Derived++; }
            """, "I value = new Derived(); value.M();");

        Runs(result, "Derived.M", "Base.M");
    }

    [Fact]
    public async Task Reimplemented_interface_uses_its_own_implementation()
    {
        var result = await Run("""
            public interface I { void M(); }
            public class Base : I { public virtual void M() => Marks.Base++; }
            public sealed class Derived : Base, I { void I.M() => Marks.Derived++; }
            """, "I value = new Derived(); value.M();");

        Runs(result, "Derived.I.M", "Base.M");
    }

    [Fact]
    public async Task Explicit_implementation_beside_an_override_wins()
    {
        var result = await Run("""
            public interface I { void M(); }
            public class Base : I { public virtual void M() => Marks.Base++; }
            public sealed class Derived : Base, I
            {
                public override void M() => Marks.Middle++;
                void I.M() => Marks.Derived++;
            }
            """, "I value = new Derived(); value.M();");

        Runs(result, "Derived.I.M", "Derived.M", "Base.M");
    }

    [Fact]
    public async Task Two_constructions_of_one_generic_interface_reach_their_own_methods()
    {
        var result = await Run("""
            public interface I<T> { void M(T value); }
            public sealed class Two : I<int>, I<string>
            {
                void I<int>.M(int value) => Marks.Base++;
                void I<string>.M(string value) => Marks.Derived++;
            }
            """, "var value = new Two(); ((I<int>)value).M(1);");

        Assert.Contains(result.Accesses, access => access.Symbol.Contains("Two.I<System.Int32>.M", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Accesses, access => access.Symbol.Contains("Two.I<System.String>.M", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Generic_class_implementing_a_generic_interface_resolves_by_substitution()
    {
        var result = await Run("""
            public interface I<T> { void M(T value); }
            public class C<T> : I<T> { public virtual void M(T value) => Marks.Base++; }
            public sealed class D : C<int> { public override void M(int value) => Marks.Derived++; }
            """, "I<int> first = new C<int>(); I<int> second = new D(); first.M(1); second.M(2);");

        Assert.Contains(result.Accesses, access => access.Symbol.Contains("C<T>.M", StringComparison.Ordinal));
        Assert.Contains(result.Accesses, access => access.Symbol.Contains("D.M", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explicit_base_implementation_runs_for_a_derived_object()
    {
        var result = await Run("""
            public interface I { void M(); }
            public class Base : I { void I.M() => Marks.Base++; }
            public sealed class Derived : Base { public void M() => Marks.Derived++; }
            """, "I value = new Derived(); value.M();");

        Runs(result, "Base.I.M", "Derived.M");
    }

    [Fact]
    public async Task Generic_interface_member_reaches_the_override()
    {
        var result = await Run("""
            public interface I<T> { void M(T value); }
            public class Base<T> : I<T> { public virtual void M(T value) => Marks.Base++; }
            public sealed class Derived : Base<int> { public override void M(int value) => Marks.Derived++; }
            """, "I<int> value = new Derived(); value.M(1);");

        Runs(result, "Derived.M", "Base<T>.M");
    }

    [Fact]
    public async Task Covariant_interface_call_reaches_its_implementation()
    {
        var result = await Run("""
            public interface I<out T> { T M(); }
            public sealed class C : I<string> { public string M() { Marks.Derived++; return "value"; } }
            """, "I<object> value = new C(); value.M();");

        Runs(result, "C.M");
        Assert.Empty(result.Coverage[0].Gaps);
    }

    [Fact]
    public async Task Contravariant_interface_call_reaches_its_implementation()
    {
        var result = await Run("""
            public interface I<in T> { void M(T value); }
            public sealed class C : I<object> { public void M(object value) => Marks.Derived++; }
            """, "I<string> value = new C(); value.M(\"value\");");

        Runs(result, "C.M");
        Assert.Empty(result.Coverage[0].Gaps);
    }

    [Fact]
    public async Task Invariant_interface_does_not_dispatch_through_another_construction()
    {
        var result = await Run("""
            public interface I<T> { void M(); }
            public sealed class C : I<string> { public void M() => Marks.Derived++; }
            """, "I<object> value = (I<object>)(object)new C(); value.M();");

        Assert.DoesNotContain(result.Accesses, access => access.Symbol.Contains("C.M", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Di_bound_interface_reaches_the_override()
    {
        var result = await Run("""
            public interface I { void M(); }
            public class Base : I { public virtual void M() => Marks.Base++; }
            public sealed class Derived : Base { public override void M() => Marks.Derived++; }
            """, "_value.M();", "private readonly I _value; public CaseController(I value) => _value = value;",
            "services.AddSingleton<I, Derived>();");

        Runs(result, "Derived.M", "Base.M");
    }

    [Fact]
    public async Task Interface_method_group_delegate_reaches_its_constructions_implementation()
    {
        var result = await Run("""
            public interface I<T> { void M(T value); }
            public sealed class Two : I<int>, I<string>
            {
                void I<int>.M(int value) => Marks.Base++;
                void I<string>.M(string value) => Marks.Derived++;
            }
            """, "var two = new Two(); Action<int> run = ((I<int>)two).M; run(1);");

        Assert.Contains(result.Accesses, access => access.Symbol.Contains("Two.I<System.Int32>.M", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Accesses, access => access.Symbol.Contains("Two.I<System.String>.M", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Interface_property_getter_and_setter_reach_the_override()
    {
        var result = await Run("""
            public interface I { int P { get; set; } }
            public class Base : I
            {
                public virtual int P { get { Marks.Base++; return 1; } set => Marks.Base++; }
            }
            public sealed class Derived : Base
            {
                public override int P { get { Marks.Derived++; return 2; } set => Marks.Middle++; }
            }
            """, "I value = new Derived(); var read = value.P; value.P = read;");

        Runs(result, "Derived.get_P", "Base.get_P", "Base.set_P");
        Assert.Contains(result.Accesses, access => access.Symbol.Contains("Derived.set_P", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Interface_indexer_reaches_the_override()
    {
        var result = await Run("""
            public interface I { int this[int index] { get; set; } }
            public class Base : I
            {
                public virtual int this[int index] { get { Marks.Base++; return 1; } set => Marks.Base++; }
            }
            public sealed class Derived : Base
            {
                public override int this[int index] { get { Marks.Derived++; return 2; } set => Marks.Middle++; }
            }
            """, "I value = new Derived(); var read = value[0]; value[0] = read;");

        Runs(result, "Derived.get_Item", "Base.get_Item", "Base.set_Item");
        Assert.Contains(result.Accesses, access => access.Symbol.Contains("Derived.set_Item", StringComparison.Ordinal));
    }

    private static async Task<AnalysisResult> Run(string types, string action, string controllerMembers = "", string registrations = "") =>
        await PhaseOneAnalyzer.AnalyzeAsync(FixtureSolution.Create(("Case.cs", Usings + $$"""
            public static class Marks { public static int Base; public static int Middle; public static int Derived; }
            {{types}}
            public sealed class CaseController : ControllerBase
            {
                {{controllerMembers}}
                public void Post() { {{action}} }
            }
            """ + Startup(registrations))), ROOT_DIRECTORY, CancellationToken.None);

    private static void Runs(AnalysisResult result, string expected, params string[] absent)
    {
        Assert.Contains(result.Accesses, access => access.Symbol.Contains(expected, StringComparison.Ordinal));
        foreach (var symbol in absent)
            Assert.DoesNotContain(result.Accesses, access => access.Symbol.Contains(symbol, StringComparison.Ordinal));
    }
}
