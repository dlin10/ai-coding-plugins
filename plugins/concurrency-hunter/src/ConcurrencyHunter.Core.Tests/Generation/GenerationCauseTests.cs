using System.Reflection;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The cases an answer names against a model entry (SPEC TD-034b, G-6): every case found, one per code, the model reason the
/// first in check order among them, and none for a member with a model.</summary>
public sealed class GenerationCauseTests
{
    private static readonly IReadOnlySet<string> CODES = typeof(ModelCauses).GetFields(BindingFlags.Public | BindingFlags.Static)
                                                                            .Select(field => (string)field.GetValue(null)!)
                                                                            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void A_probe_handed_to_an_extern_method_names_the_call()
    {
        var answer = Answer("public abstract class Node { } public static class Api { public static void Send(Node node, Action done) { Sink.Take(node); done(); } }",
                            "M:Lib.Api.Send(Lib.Node,System.Action)");

        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Assert.Contains("Lib.Sink.Take", Cause(answer, ModelCauses.PROBE_HANDED_TO_UNSEEN).Detail);
    }

    [Theory]
    [InlineData("public static string Describe(Node node, Action done) { done(); return $\"node {node}\"; }", "M:Lib.Api.Describe(Lib.Node,System.Action)",
                "Interpolation")]
    [InlineData("public static object Pair(Node node, Action done) { done(); return (node, 1); }", "M:Lib.Api.Pair(Lib.Node,System.Action)", "Tuple")]
    public void A_probe_given_to_an_operation_the_lowering_does_not_express_names_the_operation(string member, string id, string kind)
    {
        // An interpolation runs node.ToString(), user code on the probe; a tuple keeps the probe where the heap does not see it.
        var answer = Answer($"public abstract class Node {{ }} public static class Api {{ {member} }}", id);

        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Assert.Contains(kind, Cause(answer, ModelCauses.PROBE_IN_UNSUPPORTED_OPERATION).Detail);
        Assert.DoesNotContain(answer.Causes, cause => cause.Code == ModelCauses.PROBE_HANDED_TO_UNSEEN);
    }

    [Fact]
    public void A_probe_handed_to_an_extern_method_and_an_unsupported_operation_names_only_the_call()
    {
        // The lowering of the tuple would not lift the refusal: the extern call still sees the probe.
        var answer = Answer("public abstract class Node { } public static class Api { public static object Pair(Node node, Action done) { Sink.Take(node); return (node, 1); } }",
                            "M:Lib.Api.Pair(Lib.Node,System.Action)");

        Cause(answer, ModelCauses.PROBE_HANDED_TO_UNSEEN);
        Assert.DoesNotContain(answer.Causes, cause => cause.Code == ModelCauses.PROBE_IN_UNSUPPORTED_OPERATION);
    }

    [Fact]
    public void An_address_handed_to_Unsafe_goes_to_unseen_code()
    {
        // ComWrappers.GetOrRegisterObjectForComInstance: the address of a local holding the wrapper becomes a pointer in CoreLib's
        // Unsafe.AsPointer, whose body only throws until the runtime replaces it; the external call writes the result and keeps the
        // wrapper through the pointer.
        var answer = AnswerOf(GenerationRuns.PRELUDE + """
                public ref struct Handle {
                    private unsafe void* _ptr;
                    private unsafe Handle(void* p) { _ptr = p; }
                    public static unsafe Handle Create<T>(ref T o) where T : class => new Handle(System.Runtime.CompilerServices.Unsafe.AsPointer(ref o));
                }
                public sealed class Registry {
                    public object GetOrRegister(IntPtr com, object wrapper) { if (!TryGet(this, com, wrapper, out var ret)) throw new ArgumentNullException(); return ret; }
                    private static bool TryGet(Registry impl, IntPtr com, object wrapperMaybe, out object retValue) {
                        object o = wrapperMaybe;
                        retValue = null;
                        return Native(Handle.Create(ref impl), com, Handle.Create(ref o), Handle.Create(ref retValue)) != 0;
                    }
                    private static extern int Native(Handle impl, IntPtr com, Handle wrapper, Handle ret);
                }
            }

            namespace System.Runtime.CompilerServices
            {
                public static class Unsafe { public static unsafe void* AsPointer<T>(ref T value) { throw new PlatformNotSupportedException(); } }
            }
            """, "M:Lib.Registry.GetOrRegister(System.IntPtr,System.Object)");

        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Assert.Contains("Unsafe.AsPointer", Cause(answer, ModelCauses.PROBE_HANDED_TO_UNSEEN).Detail);
    }

    [Theory]
    [InlineData("var r = Choice.Value ? a : b; r[0] = 1;", "a,b")]
    [InlineData("void Local() { a[0] = 1; } Local();", "a")]
    [InlineData("Action set = () => a[0] = 1; set();", "a")]
    [InlineData("var box = new Box2 { Cells = a }; var r = box.Cells; r[0] = 1;", "a")]
    [InlineData("var all = new[] { a }; all[0][0] = 1;", "a")]
    [InlineData("Id(a)[0] = 1;", "a")]
    [InlineData("var r = Choice.Value ? a : b; ((IList<int>)r)[0] = 1;", "a,b")]
    public void A_write_into_a_cell_of_an_array_argument_any_way_it_came_writes_its_cells(string body, string written)
    {
        // A value of no object leaves the cells holding nothing the heap names, so only the access says the cells were written
        // (question 34).
        var answer = Answer("public static class Choice { public static bool Value; } public sealed class Box2 { public int[] Cells; } " +
                            $"public static class Api {{ public static void Run(int[] a, int[] b, Action done) {{ {body} done(); }} " +
                            "private static int[] Id(int[] x) => x; }", "M:Lib.Api.Run(System.Int32[],System.Int32[],System.Action)");

        Assert.True(answer.Model is not null, string.Join("; ", answer.Causes));
        Assert.Equal(written.Split(',').Select(LibraryEffect.WriteCellsOf).ToHashSet(), answer.Model.Effects.ToHashSet());
    }

    [Fact]
    public void An_operation_the_lowering_does_not_express_that_no_probe_reaches_leaves_the_model()
    {
        var answer = Answer("public static class Api { public static string Describe(int count, Action done) { done(); return $\"count {count}\"; } }",
                            "M:Lib.Api.Describe(System.Int32,System.Action)");

        Assert.NotNull(answer.Model);
        Assert.Empty(answer.Causes);
    }

    [Theory]
    [InlineData("public interface IStore { bool Has(object key); void Put(object key, object value); } public static class Api { " +
                "public static bool TryPut(IStore store, object key, object value) { if (store.Has(key)) return false; store.Put(key, value); return true; } }",
                "M:Lib.Api.TryPut(Lib.IStore,System.Object,System.Object)", "IStore")]
    [InlineData("public static class Api { public static void Fill(System.IO.Stream stream, byte[] data) => stream.Write(data, 0, data.Length); }",
                "M:Lib.Api.Fill(System.IO.Stream,System.Byte[])", "Write")]
    [InlineData("public abstract class Converter { public abstract void Convert(); } public sealed class Options { public Converter Converter; } " +
                "public static class Api { public static void Run(Options options, Action done) { options.Converter.Convert(); done(); } }",
                "M:Lib.Api.Run(Lib.Options,System.Action)", "Convert")]
    [InlineData("public sealed class Options { public Action<object> Use; } public sealed class Value { } " +
                "public static class Api { public static void Run(Options options, Value value) => options.Use(value); }",
                "M:Lib.Api.Run(Lib.Options,Lib.Value)", "Invoke")]
    public void A_member_of_an_argument_called_through_a_witness_names_the_member(string types, string id, string member)
    {
        // The witness stands for code the analysis does not see: a Dictionary, a MemoryStream or a user class may write what it gets.
        var answer = Answer(types, id);

        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Assert.Contains(member, Cause(answer, ModelCauses.ARGUMENT_MEMBER_UNSEEN).Detail);
    }

    [Theory]
    [InlineData("public static void Same(Node node, Node other) { _ = node.Equals(other); }", "M:Lib.Api.Same(Lib.Node,Lib.Node)",
                "Equals(arg:node|arg:other)")]
    [InlineData("public static void Hash(Node node) { _ = node.GetHashCode(); }", "M:Lib.Api.Hash(Lib.Node)", "GetHashCode(arg:node|)")]
    [InlineData("public static void Order(IComparer<Node> comparer, Node a, Node b) { _ = comparer.Compare(a, b); }",
                "M:Lib.Api.Order(System.Collections.Generic.IComparer{Lib.Node},Lib.Node,Lib.Node)", "Compare(arg:comparer|arg:a|arg:b)")]
    [InlineData("public static bool Less<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) < 0;", "M:Lib.Api.Less``1(``0,``0)",
                "CompareTo(arg:a|arg:b)")]
    [InlineData("public static void Same<T>(T a, T b) where T : IEquatable<T> { _ = a.Equals(b); }", "M:Lib.Api.Same``1(``0,``0)",
                "Equals(arg:a|arg:b)")]
    [InlineData("public static void Hash(IEqualityComparer<Node> comparer, Node node) { _ = comparer.GetHashCode(node); }",
                "M:Lib.Api.Hash(System.Collections.Generic.IEqualityComparer{Lib.Node},Lib.Node)", "GetHashCode(arg:comparer|arg:node)")]
    public void A_member_of_equality_hash_or_ordering_a_witness_stood_for_is_a_call(string member, string id, string call)
    {
        // The model says which of the user's members the call runs, on what and with what, beside the deep read (question 47).
        var answer = Answer($"public abstract class Node {{ public abstract string Name {{ get; }} }} public static class Api {{ {member} }}", id);

        Assert.True(answer.Model is not null, string.Join("; ", answer.Causes));
        Assert.Equal(call, Assert.Single(answer.Model.Calls).Canonical);
    }

    [Theory]
    [InlineData("public static void Log(Node node, Action done) { _ = node.ToString(); done(); }", "M:Lib.Api.Log(Lib.Node,System.Action)")]
    [InlineData("public static void Look(Node node, Action done) { _ = node.Name; done(); }", "M:Lib.Api.Look(Lib.Node,System.Action)")]
    public void A_getter_or_ToString_a_witness_stood_for_is_no_call(string member, string id)
    {
        var answer = Answer($"public abstract class Node {{ public abstract string Name {{ get; }} }} public static class Api {{ {member} }}", id);

        Assert.True(answer.Model is not null, string.Join("; ", answer.Causes));
        Assert.Empty(answer.Model.Calls);
    }

    [Theory]
    [InlineData("public class Reader { public virtual int Read(char[] buffer) { buffer[0] = 'x'; return 1; } public void Fill(char[] buffer) { Read(buffer); } }",
                "M:Lib.Reader.Fill(System.Char[])", "Read")]
    [InlineData("public class Handle { private object _state = new object(); public void Close() { Dispose(true); } " +
                "protected virtual void Dispose(bool disposing) { _state = null; } }", "M:Lib.Handle.Close", "Dispose")]
    [InlineData("public abstract class Target { public abstract void Put(object o); } public sealed class Writer { public Target Target; " +
                "public void Write(object o) => Target.Put(o); }", "M:Lib.Writer.Write(System.Object)", "Put")]
    public void A_member_of_the_receiver_called_through_a_witness_names_the_member(string types, string id, string member)
    {
        // StringReader.ReadAsync calls the virtual Read, and Close the virtual Dispose(bool): a witness of the receiver's subclass
        // stands for an override the analysis does not see and for the library's own body, which writes; a seed in the receiver's
        // field stands for whatever object a program put there (question 98).
        var answer = Answer(types, id);

        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Assert.Contains(member, Cause(answer, ModelCauses.RECEIVER_MEMBER_UNSEEN).Detail);
    }

    [Fact]
    public void A_struct_receivers_own_override_runs_in_the_call()
    {
        // The driver's receiver is a struct in a static field, which has no object for a dispatch to find: the call once ran nothing,
        // and the answer was {} whatever the body did.
        var answer = Answer("public struct Pair<T1, T2> { public T1 Item1; public T2 Item2; public override int GetHashCode() => Item1.GetHashCode(); } " +
                            "public struct Mark { public object Item; public override string ToString() { Sink.Take(Item); return \"m\"; } }",
                            "M:Lib.Mark.ToString");

        Assert.Null(answer.Model);
        Assert.Equal(ModelReasons.INCOMPLETE, answer.ModelReason);
    }

    [Fact]
    public void A_type_initializer_assigning_its_own_seeded_static_touches_no_user_object()
    {
        // Array.Empty<T>(): setup seeds EmptyArray<T>.Value, and the type initializer's own store of it ran in an execution of its
        // own, which once read as a library touching a probe elsewhere. A type initializer runs before any program code could put
        // anything into its type's statics (question 158).
        var answer = Answer("public static class Empty<T> { public static readonly T[] Value = new T[0]; } " +
                            "public static class Api { public static int Count<T>(T item, Action done) { var empty = Empty<T>.Value; done(); return empty.Length; } }",
                            "M:Lib.Api.Count``1(``0,System.Action)");

        Assert.True(answer.Model is not null, string.Join("; ", answer.Causes));
        Assert.DoesNotContain(answer.Causes, cause => cause.Code == ModelCauses.PROBE_ACCESSED_ELSEWHERE);
    }

    private const string RAW = "public abstract class Node { public Node Next; } public sealed class Slot { public Node Node; } " +
                               "public struct Mem { public object O; } public struct Mem2 { public object O; } " +
                               "public static class Raw { public static extern ref TTo As<TFrom, TTo>(ref TFrom source); " +
                               "public static extern ref Node At(ref Node start); public static extern void Touch(ref Node slot); " +
                               "public static extern void Bump(ref int count); } ";

    [Theory]
    [InlineData("public static Mem2 Reinterpret(Mem m) => Raw.As<Mem, Mem2>(ref m);", "M:Lib.Api.Reinterpret(Lib.Mem)")]
    [InlineData("public static Node Element(Node[] nodes) => Raw.At(ref nodes[0]);", "M:Lib.Api.Element(Lib.Node[])")]
    public void What_is_read_through_a_reference_a_bodyless_call_returns_is_unobserved(string member, string id)
    {
        // MemoryMarshal.AsMemory is Unsafe.As<ReadOnlyMemory<T>, Memory<T>>(ref memory): no body says where the reference points,
        // so what is read through it is a place nothing proves, never "no object" (question 160).
        var answer = Answer(RAW + $"public static class Api {{ {member} }}", id);

        Assert.Null(answer.Model);
        Assert.Contains(answer.Causes, cause => cause.Code == ModelCauses.VALUE_UNOBSERVED);
    }

    [Theory]
    [InlineData("public static void TouchElement(Node[] nodes, Action done) { Raw.Touch(ref nodes[0]); done(); }",
                "M:Lib.Api.TouchElement(Lib.Node[],System.Action)")]
    [InlineData("public static void TouchField(Node node, Action done) { Raw.Touch(ref node.Next); done(); }",
                "M:Lib.Api.TouchField(Lib.Node,System.Action)")]
    public void A_reference_to_an_inputs_storage_handed_to_a_bodyless_call_hands_the_object_the_storage_lies_in(string member, string id)
    {
        // The call may read and write the cell or field the reference names, as it may any storage of an object handed to it
        // (question 160).
        var answer = Answer(RAW + $"public static class Api {{ {member} }}", id);

        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Assert.Contains("Lib.Raw.Touch", Cause(answer, ModelCauses.PROBE_HANDED_TO_UNSEEN).Detail);
    }

    [Theory]
    [InlineData("public static void Give(int[] counts, Action done) { Sink.Take(counts); done(); }", "M:Lib.Api.Give(System.Int32[],System.Action)",
                "Lib.Sink.Take")]
    [InlineData("public static void BumpElement(int[] counts, Action done) { Raw.Bump(ref counts[0]); done(); }",
                "M:Lib.Api.BumpElement(System.Int32[],System.Action)", "Lib.Raw.Bump")]
    [InlineData("public static void GiveField(Slot slot, Action done) { Sink.Take(slot.Node); done(); }",
                "M:Lib.Api.GiveField(Lib.Slot,System.Action)", "Lib.Sink.Take")]
    [InlineData("public static void GiveSlot(Slot slot, Action done) { Sink.Take(slot); done(); }", "M:Lib.Api.GiveSlot(Lib.Slot,System.Action)",
                "Lib.Sink.Take")]
    public void An_input_handed_to_a_bodyless_call_is_touched_though_it_is_no_probe(string member, string id, string callee)
    {
        // Marshal.Copy writes the user's byte[] where the analysis does not look: an argument, the receiver, a seed and what setup
        // built into them are the user's objects as much as a probe is (question 161).
        var answer = Answer(RAW + $"public static class Api {{ {member} }}", id);

        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Assert.Contains(callee, Cause(answer, ModelCauses.PROBE_HANDED_TO_UNSEEN).Detail);
    }

    [Fact]
    public void An_input_given_to_an_operation_the_lowering_does_not_express_is_touched_though_it_is_no_probe()
    {
        // Task.WhenAny(p) builds its params span with a collection expression, which the lowering does not express: what it does with
        // the task is not seen (question 161).
        var answer = Answer("public static class Api { public static Task<Task<Box>> Any(Task<Box> p) => Task.WhenAny(p); }",
                            "M:Lib.Api.Any(System.Threading.Tasks.Task{Lib.Box})");

        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Assert.Contains("CollectionExpression", Cause(answer, ModelCauses.PROBE_IN_UNSUPPORTED_OPERATION).Detail);
    }

    [Theory]
    [InlineData("public sealed class Comparer { public override int GetHashCode() => GetType().GetHashCode(); }", "M:Lib.Comparer.GetHashCode")]
    [InlineData("public static class Api { public static string Name(Node node, Action done) { var type = node.GetType(); done(); return type.Name; } }",
                "M:Lib.Api.Name(Lib.Node,System.Action)")]
    public void GetType_touches_nothing_of_the_object_it_runs_on(string types, string id)
    {
        // Generating CoreLib, object.GetType() is a member of the library itself, extern and without a model; it reads only the
        // object's type, which never changes (question 161).
        var library = GenerationRuns.Compile(GenerationRuns.PRELUDE + RAW + types + "\n}\n");
        var models = new LibraryModels(LibraryModels.BuiltIn.Members.Where(model => !model.Id.StartsWith("M:System.Object.GetType", StringComparison.Ordinal)),
                                       LibraryModels.BuiltIn.ImmutableTypes);
        var answer = ModelGenerator.Trace(new GenerationRequest(GenerationRuns.ASSEMBLY, "1.0", id, null, null), library, CancellationToken.None, models)
                                   .Answer;

        Assert.True(answer.Model is not null, string.Join("; ", answer.Causes));
    }

    [Fact]
    public void An_immutable_input_handed_to_a_bodyless_call_leaves_the_model()
    {
        // No code can write a Version, a Type or a string: reflection hands RuntimeType objects to calls with no body throughout.
        var answer = Answer(RAW + "public static class Api { public static void Log(Version version, Action done) { Sink.Take(version); done(); } }",
                            "M:Lib.Api.Log(System.Version,System.Action)");

        Assert.True(answer.Model is not null, string.Join("; ", answer.Causes));
    }

    [Fact]
    public void A_member_whose_body_the_call_never_reached_has_no_answer()
    {
        // The receiver's recipe is the factory, which makes a subclass overriding the member: the call runs Leaf.Size, never Node.Size.
        var trace = GenerationRuns.Trace("public class Node { public object Value; internal Node() { } public static Node Make() => new Leaf(); " +
                                         "public virtual int Size(Action done) { done(); return 1; } } " +
                                         "public sealed class Leaf : Node { public override int Size(Action done) => 2; }",
                                         "M:Lib.Node.Size(System.Action)");

        Assert.Equal(GenerationReasons.ANALYSIS_FAILED, trace.Answer.Reason);
        Assert.Contains("never reached", trace.Answer.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public sealed class Cell<T> { public T Value; public Cell(T v) { Value = v; } public override string ToString() => Value?.ToString() ?? \"\"; }",
                "M:Lib.Cell`1.ToString")]
    [InlineData("public abstract class Part { public abstract string Name { get; } } public sealed class Cell { public Part Value; public Cell(Part v) { Value = v; } " +
                "public string Describe() => Value.Name; }", "M:Lib.Cell.Describe")]
    [InlineData("public abstract class Part { public abstract string Name { get; } } public class Frame { public Part Part; public string Describe() => Part.Name; }",
                "M:Lib.Frame.Describe")]
    public void A_read_of_what_the_receiver_holds_is_a_deep_read_of_this(string types, string id)
    {
        // The receiver holds a user object — a constructor's argument or a seed — whose getter or ToString runs on the read: the model
        // may not say {} (question 80).
        var answer = Answer(types, id);

        Assert.True(answer.Model is not null, string.Join("; ", answer.Causes));
        Assert.Contains(LibraryEffect.DeepReadOf("this"), answer.Model.Effects);
    }

    [Fact]
    public void An_equality_member_of_what_the_receiver_holds_has_no_name_to_run_on()
    {
        // ValueTuple<…>.GetHashCode runs GetHashCode of the objects the receiver holds, which no value of the vocabulary names.
        var answer = Answer("public sealed class Cell<T> { public T Value; public Cell(T v) { Value = v; } public override int GetHashCode() => Value?.GetHashCode() ?? 0; }",
                            "M:Lib.Cell`1.GetHashCode");

        Assert.Null(answer.Model);
        Assert.Contains("CallValue", Cause(answer, ModelCauses.VALUE_UNNAMED).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_read_of_the_receivers_own_state_names_no_effect()
    {
        var answer = Answer("public class Counter { private int _count; public object Last; public int Count() => _count; }", "M:Lib.Counter.Count");

        Assert.True(answer.Model is not null, string.Join("; ", answer.Causes));
        Assert.Empty(answer.Model.Effects);
    }

    [Fact]
    public void A_getter_of_the_receiver_the_analysis_takes_to_read_keeps_the_model_through_a_witness()
    {
        var answer = Answer("public class Box3 { public object Value; public virtual int Size => 1; public void Check(Action done) { if (Size > 0) done(); } }",
                            "M:Lib.Box3.Check(System.Action)");

        Assert.True(answer.Model is not null, string.Join("; ", answer.Causes));
    }

    [Theory]
    [InlineData("public static void Log(Node node, Action done) { _ = node.ToString(); done(); }", "M:Lib.Api.Log(Lib.Node,System.Action)", "node")]
    [InlineData("public static void Same(Node node, Node other) { _ = node.Equals(other); }", "M:Lib.Api.Same(Lib.Node,Lib.Node)", "other")]
    [InlineData("public static void Hash(Node node) { _ = node.GetHashCode(); }", "M:Lib.Api.Hash(Lib.Node)", "node")]
    [InlineData("public static void Look(Node node, Action done) { _ = node.Name; done(); }", "M:Lib.Api.Look(Lib.Node,System.Action)", "node")]
    [InlineData("public static void Order(IComparer<Node> comparer, Node a, Node b) { _ = comparer.Compare(a, b); }",
                "M:Lib.Api.Order(System.Collections.Generic.IComparer{Lib.Node},Lib.Node,Lib.Node)", "a")]
    [InlineData("public static bool Less<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) < 0;", "M:Lib.Api.Less``1(``0,``0)", "b")]
    [InlineData("public static void Same<T>(T a, T b) where T : IEquatable<T> { _ = a.Equals(b); }", "M:Lib.Api.Same``1(``0,``0)", "b")]
    [InlineData("public static void Hash(IEqualityComparer<Node> comparer, Node node) { _ = comparer.GetHashCode(node); }",
                "M:Lib.Api.Hash(System.Collections.Generic.IEqualityComparer{Lib.Node},Lib.Node)", "node")]
    public void A_member_the_analysis_takes_to_read_keeps_the_model_through_a_witness(string member, string id, string read)
    {
        // A getter, ToString, Equals, GetHashCode and a member of comparison read what they get, as deep read takes user code to. The
        // members return no witness value, which is no object a result could name.
        var answer = Answer($"public abstract class Node {{ public abstract string Name {{ get; }} }} public static class Api {{ {member} }}", id);

        Assert.True(answer.Model is not null, string.Join("; ", answer.Causes));
        Assert.Contains(LibraryEffect.DeepReadOf(read), answer.Model.Effects);
    }

    [Fact]
    public void What_a_witness_gives_back_through_out_is_no_longer_lost()
    {
        // The receiver's own TryNext may give back any item: the model may not say the result is only the fallback. Calling it is
        // also code the analysis does not see, which refuses first.
        var answer = Answer("public sealed class Item { } public abstract class Source { public abstract bool TryNext(out Item item); " +
                            "public Item NextOr(Item fallback) => TryNext(out var item) ? item : fallback; }",
                            "M:Lib.Source.NextOr(Lib.Item)");

        Assert.Null(answer.Model);
        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Cause(answer, ModelCauses.RECEIVER_MEMBER_UNSEEN);
        Assert.Contains(answer.Causes, cause => cause.Reason == ModelReasons.VOCABULARY);
    }

    [Fact]
    public void A_load_of_an_unseeded_field_names_the_field()
    {
        var answer = Answer("public struct Part { public object Value; } public sealed class Options { public Part Part; } " +
                            "public static class Api { public static void Read(Options options, Action done) { _ = options.Part.Value; done(); } }",
                            "M:Lib.Api.Read(Lib.Options,System.Action)");

        Assert.Equal(ModelReasons.INCOMPLETE, answer.ModelReason);
        Assert.Contains("Lib.Part.Value", Cause(answer, ModelCauses.UNSEEDED_READ).Detail);
    }

    [Fact]
    public void A_store_into_a_library_static_names_the_store()
    {
        var answer = Answer("public static class Api { public static void Run(Action done) { Cache.Last = new object(); done(); } }",
                            "M:Lib.Api.Run(System.Action)");

        Assert.Equal(ModelReasons.LIBRARY_STATE, answer.ModelReason);
        Assert.Contains("Lib.Cache", Cause(answer, ModelCauses.LIBRARY_STATE_STORE).Detail);
    }

    [Fact]
    public void A_read_of_what_a_user_delegate_returned_names_the_field()
    {
        var answer = Answer("public sealed class Item { public object Value; } " +
                            "public static class Api { public static object Run(Func<Item> make) => make().Value; }",
                            "M:Lib.Api.Run(System.Func{Lib.Item})");

        Assert.Equal(ModelReasons.VOCABULARY, answer.ModelReason);
        Assert.Contains("Lib.Item.Value", Cause(answer, ModelCauses.CALLBACK_VALUE_READ).Detail);
    }

    [Fact]
    public void A_member_with_a_model_names_no_cause()
    {
        var answer = Answer("public static class Api { public static void Run(Action done) { done(); } }", "M:Lib.Api.Run(System.Action)");

        Assert.NotNull(answer.Model);
        Assert.Empty(answer.Causes);
    }

    [Fact]
    public void A_case_of_a_later_reason_is_named_beside_the_first()
    {
        // The probe is handed to code the analysis cannot follow, and a field of what the delegate returned is read too: unknown-touch
        // comes first, and the vocabulary case stays named.
        var answer = Answer("public abstract class Node { } public sealed class Item { public object Value; } public static class Api { " +
                            "public static object Send(Node node, Func<Item> make) { Sink.Take(node); return make().Value; } }",
                            "M:Lib.Api.Send(Lib.Node,System.Func{Lib.Item})");

        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Cause(answer, ModelCauses.PROBE_HANDED_TO_UNSEEN);
        Cause(answer, ModelCauses.CALLBACK_VALUE_READ);
    }

    [Fact]
    public void Library_state_is_not_looked_for_once_unknown_touch_is_found()
    {
        // Its check walks the keeping paths of every state store, which the generation spends only while the reason is still open.
        var answer = Answer("public abstract class Node { } public static class Api { " +
                            "public static void Send(Node node, Action done) { Sink.Take(node); Cache.Last = new object(); done(); } }",
                            "M:Lib.Api.Send(Lib.Node,System.Action)");

        Assert.Equal(ModelReasons.UNKNOWN_TOUCH, answer.ModelReason);
        Assert.DoesNotContain(answer.Causes, cause => cause.Code == ModelCauses.LIBRARY_STATE_STORE);
    }

    [Fact]
    public void A_case_met_again_formats_its_evidence_once()
    {
        var causes = new GenerationCauses();
        var formatted = 0;
        string Evidence() => $"evidence {++formatted}";

        causes.Add(ModelReasons.VOCABULARY, ModelCauses.VALUE_UNNAMED, $"{Evidence()}");
        causes.Add(ModelReasons.VOCABULARY, ModelCauses.VALUE_UNNAMED, $"{Evidence()}");

        Assert.Equal(1, formatted);
        Assert.Equal("value-unnamed: evidence 1", Assert.Single(causes.All).ToString());
    }

    /// <summary>The answer for one member of a fixture library, checked against what every answer with causes holds: each code is
    /// one of <see cref="ModelCauses"/>, the causes are sorted by code and one per code, and the model reason is the first reason in
    /// check order among them.</summary>
    /// <param name="types">Declarations inside namespace <c>Lib</c>.</param>
    /// <param name="memberId">The member's declaration id.</param>
    private static GeneratedAnswer Answer(string types, string memberId) => AnswerOf(GenerationRuns.PRELUDE + types + "\n}\n", memberId);

    /// <summary>The answer for one member of a fixture library of exactly <paramref name="source"/>, checked as
    /// <see cref="Answer"/> checks it.</summary>
    /// <param name="source">The library's source.</param>
    /// <param name="memberId">The member's declaration id.</param>
    private static GeneratedAnswer AnswerOf(string source, string memberId)
    {
        var answer = GenerationRuns.TraceOf(source, memberId).Answer;
        Assert.All(answer.Causes, cause => Assert.Contains(cause.Code, CODES));
        Assert.Equal(answer.Causes.Select(cause => cause.Code).Order(StringComparer.Ordinal).Distinct(), answer.Causes.Select(cause => cause.Code));
        Assert.Equal(ModelReasons.Ordered.FirstOrDefault(reason => answer.Causes.Any(cause => cause.Reason == reason)), answer.ModelReason);
        return answer;
    }

    /// <summary>The one cause of a code an answer names.</summary>
    /// <param name="answer">The answer.</param>
    /// <param name="code">The code.</param>
    private static GenerationCause Cause(GeneratedAnswer answer, string code) =>
        Assert.Single(answer.Causes, cause => cause.Code == code);
}
