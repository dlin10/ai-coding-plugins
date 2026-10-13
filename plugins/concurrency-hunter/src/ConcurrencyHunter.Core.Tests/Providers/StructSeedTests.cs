using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>Setup seeds the reference fields of a struct receiver and of a by-value struct argument along their paths, as it seeds an
/// object's fields (SPEC TD-034b, R8): through the struct's own place, which a static field, a writable field of an object or of such a
/// struct and a one-dimensional array's cell are, to the same depth and with the same opening. A seed so placed is reached, and the
/// member answers as its class twin does. A struct a property or a readonly field gives is a copy: what it holds stays unseeded.</summary>
public sealed class StructSeedTests
{
    [Fact]
    public void A_receiver_structs_reference_field_is_seeded_in_place_and_answers_as_its_class_twin()
    {
        const string TYPES = "{0} Cell {{ public object Value; public string Show() => Value.ToString(); }}";
        var trace = Run(string.Format(TYPES, "public struct"), "M:Lib.Cell.Show");

        Assert.Contains("Recv_Call.Value = new Seed_", Setup(trace));
        Seeded(trace, "this/F:Lib.Cell.Value");
        Assert.Equal(Shape(Run(string.Format(TYPES, "public sealed class"), "M:Lib.Cell.Show")), Shape(trace));
    }

    [Fact]
    public void An_argument_structs_reference_field_is_seeded_in_place_and_answers_as_its_class_twin()
    {
        const string TYPES = "{0} Pair {{ public object A; }} public static class Api {{ public static string Show(Pair pair) => pair.A.ToString(); }}";
        var trace = Run(string.Format(TYPES, "public struct"), "M:Lib.Api.Show(Lib.Pair)");

        Assert.Contains("Arg_pair_Call.A = new Seed_", Setup(trace));
        Seeded(trace, "arg:pair/F:Lib.Pair.A");
        Assert.Equal(Shape(Run(string.Format(TYPES, "public sealed class"), "M:Lib.Api.Show(Lib.Pair)")), Shape(trace));
    }

    [Fact]
    public void A_nested_structs_reference_field_is_seeded_through_every_struct_field()
    {
        const string ID = "M:Lib.Api.Show(Lib.Outer)";
        var trace = Run("public struct Inner { public object Value; } public struct Outer { public Inner Inner; } " +
                          "public static class Api { public static string Show(Outer outer) => outer.Inner.Value.ToString(); }", ID);
        var twin = Run("public sealed class Inner { public object Value; } public sealed class Outer { public Inner Inner = new(); } " +
                         "public static class Api { public static string Show(Outer outer) => outer.Inner.Value.ToString(); }", ID);

        Assert.Contains("Arg_outer_Call.Inner.Value = new Seed_", Setup(trace));
        Seeded(trace, "arg:outer/F:Lib.Outer.Inner/F:Lib.Inner.Value");
        Assert.Equal(Shape(twin), Shape(trace));
    }

    [Fact]
    public void An_auto_propertys_backing_field_is_seeded_through_its_opened_setter()
    {
        const string TYPES = "{0} Part {{ private object Secret {{ get; }} public string Show() => Secret.ToString(); }}";
        var trace = Run(string.Format(TYPES, "public struct"), "M:Lib.Part.Show");

        Assert.Contains("Recv_Call.Secret = new Seed_", Setup(trace));
        Seeded(trace, "this/P:Lib.Part.Secret");
        Assert.Equal(Shape(Run(string.Format(TYPES, "public sealed class"), "M:Lib.Part.Show")), Shape(trace));
    }

    [Fact]
    public void A_generic_readonly_structs_fields_like_KeyValuePairs_are_opened_and_seeded_with_its_probes()
    {
        // KeyValuePair<TKey, TValue>: a readonly struct whose readonly fields no setup could write until the opening dropped the
        // struct's readonly and kept every member of it readonly.
        const string TYPES = "{0} Entry<TKey, TValue> {{ private readonly TKey _key; private readonly TValue _value; " +
                             "public Entry(TKey key, TValue value) {{ _key = key; _value = value; }} " +
                             "public override string ToString() => _key.ToString() + _value.ToString(); }}";
        var trace = Run(string.Format(TYPES, "public readonly struct"), "M:Lib.Entry`2.ToString");

        Assert.Contains("Recv_Call._key = new Probe_TKey()", Setup(trace));
        Assert.Contains("Recv_Call._value = new Probe_TValue()", Setup(trace));
        Seeded(trace, "this/F:Lib.Entry`2._key");
        Seeded(trace, "this/F:Lib.Entry`2._value");
        Assert.Equal(Shape(Run(string.Format(TYPES, "public sealed class"), "M:Lib.Entry`2.ToString")), Shape(trace));
    }

    [Theory]
    [InlineData("public readonly record struct", "public readonly struct", "S { private readonly object value; public string Show() => value.ToString(); }")]
    [InlineData("public readonly record struct", "public readonly struct",
                "S(object tag) { private readonly object value = tag; public string Show() => value.ToString(); }")]
    [InlineData("public record struct", "public struct", "S { private readonly object value; public string Show() => value.ToString(); }")]
    public void A_record_structs_fields_are_opened_and_seeded_as_its_plain_struct_twins(string record, string twin, string declaration)
    {
        // A struct is opened by what it is, not by the syntax declaring it: a readonly record struct loses readonly as a readonly struct
        // does, so the field it keeps is seeded and the member answers as the twin's (R8).
        var trace = Run($"{record} {declaration}", "M:Lib.S.Show");
        var plain = Run($"{twin} {declaration}", "M:Lib.S.Show");

        Seeded(plain, "this/F:Lib.S.value");
        Seeded(trace, "this/F:Lib.S.value");
        Assert.Equal(Shape(plain), Shape(trace));
    }

    [Theory]
    [InlineData("w.Ref = p; _s = w;", false)]
    [InlineData("if (flag) w.Ref = p; _s = w;", true)]
    [InlineData("if (w.Ref == null) return; w.Ref = p; _s = w;", true)]
    public void A_seed_the_member_overwrites_before_any_use_is_not_stored_with_the_whole_struct(string body, bool keepsSeed)
    {
        // A by-value struct argument's field the member sets before anything else uses the argument holds only what was set when the
        // argument is stored whole (R2, R7); a path that may use it first, or skip the store, keeps what setup seeded there.
        var trace = Run("public struct Tagged { public int Count; public object Ref; } " +
                        $"public sealed class Shelf {{ private Tagged _s; public void M(object p, Tagged w, bool flag) {{ {body} }} }}",
                        "M:Lib.Shelf.M(System.Object,Lib.Tagged,System.Boolean)");

        var entry = trace.Answer.Model is { } model ? ModelEntryWriter.Write(model).ToJsonString() : Shape(trace);
        Assert.Contains("\"arg:p\"", entry, StringComparison.Ordinal);
        Assert.True(keepsSeed == entry.Contains("\"arg:w\"", StringComparison.Ordinal), entry);
    }

    [Fact]
    public void A_ValueTask_like_structs_object_is_seeded_and_the_runtimes_ValueTask_object_stays_unseeded()
    {
        // ValueTaskAwaiter.IsCompleted reads the user's IValueTaskSource from ValueTask._obj. In the library that declares it the
        // field is opened and seeded; the runtime's own ValueTask, another assembly's, keeps its internal field from setup.
        const string TYPES = "public interface ISource {{ int Status(); }} public sealed class Done : ISource {{ public int Status() => 0; }} " +
                             "{0} ValueTask {{ internal readonly object _obj; internal readonly short _token; " +
                             "public ValueTask(ISource source, short token) {{ _obj = source; _token = token; }} " +
                             "public bool IsCompleted => _obj is ISource source ? source.Status() == 0 : true; }}";
        var trace = Run(string.Format(TYPES, "public readonly struct"), "M:Lib.ValueTask.get_IsCompleted");
        var runtime = Run("public struct Op { public System.Threading.Tasks.ValueTask Pending; public object Tag; " +
                            "public string Show() => Tag.ToString(); }", "M:Lib.Op.Show");

        Seeded(trace, "this/F:Lib.ValueTask._obj");
        Assert.Equal(Shape(Run(string.Format(TYPES, "public sealed class"), "M:Lib.ValueTask.get_IsCompleted")), Shape(trace));
        Seeded(runtime, "this/F:Lib.Op.Tag");
        Assert.Contains("this/F:Lib.Op.Pending/F:System.Threading.Tasks.ValueTask._obj", runtime.Driver!.Unseeded);
    }

    [Fact]
    public void A_cell_of_a_struct_array_is_a_place_its_fields_are_seeded_through()
    {
        var trace = Run("public struct Slot { public object Value; } " +
                          "public static class Api { public static string Show(Slot[] slots) => slots[0].Value.ToString(); }",
                          "M:Lib.Api.Show(Lib.Slot[])");

        Assert.Contains("Arg_slots_Call[SeedElement_", Setup(trace));
        Seeded(trace, "arg:slots/F:Lib.Slot.Value");
    }

    [Theory]
    [InlineData("public Slot Slot { get; set; }", "P:Lib.Keeper.Slot")]
    [InlineData("public readonly Slot Slot;", "F:Lib.Keeper.Slot")]
    public void A_struct_a_property_or_a_readonly_field_gives_is_a_copy_whose_fields_stay_unseeded(string declaration, string id)
    {
        var trace = Run($"public struct Slot {{ public object Value; }} public sealed class Keeper {{ {declaration} }} " +
                          "public static class Api { public static string Show(Keeper keeper) => keeper.Slot.Value.ToString(); }",
                          "M:Lib.Api.Show(Lib.Keeper)");

        Assert.Contains($"arg:keeper/{id}/F:Lib.Slot.Value", trace.Driver!.Unseeded);
        Assert.DoesNotContain(trace.Driver.SeedStatements, seed => seed.Path.StartsWith($"arg:keeper/{id}", StringComparison.Ordinal));
    }

    /// <summary>Asserts that setup seeds the path, does not list it unseeded, and that its seed reaches an object.</summary>
    /// <param name="trace">The trace, with a driver and a completed run.</param>
    /// <param name="path">The field path.</param>
    private static void Seeded(GenerationTrace trace, string path)
    {
        Assert.Contains(trace.Driver!.SeedStatements, seed => seed.Path == path);
        Assert.DoesNotContain(path, trace.Driver.Unseeded);
        Assert.DoesNotContain(path, new EffectReader(trace.Driver, trace.Run!).UnreachedSeeds);
    }

    private static GenerationTrace Run(string types, string memberId)
    {
        var trace = Trace(types, memberId);
        Assert.True(trace.Driver is not null && trace.Run is { Stopped: false }, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        return trace;
    }

    /// <summary>An answer without what names its member: the entry, or the model reason.</summary>
    /// <param name="trace">The trace.</param>
    private static string Shape(GenerationTrace trace) => trace.Answer.Model is { } model
        ? ModelEntryWriter.Write(model with { Id = "M" }).ToJsonString()
        : trace.Answer.ModelReason ?? trace.Answer.Reason ?? "none";

    private static string Setup(GenerationTrace trace) => trace.Driver!.Compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
                                                               .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                                                               .Single(method => method.Identifier.Text == DriverSynthesizer.SETUP).Body!.ToString();
}
