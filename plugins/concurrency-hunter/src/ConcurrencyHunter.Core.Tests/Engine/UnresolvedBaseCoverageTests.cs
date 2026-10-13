using ConcurrencyHunter.Accesses;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>An access whose base should name heap storage and resolves to no region is counted by a counter of its own, whatever its
/// form, and no such access is dropped without a count (R6). One left out on purpose is not that counter's: one on a struct copy, a
/// construction-local one, a fresh one, one on a collection no field holds. A resolved base, and an immutable object an effect touches
/// nothing of, stay uncounted too. Each row is one access added to an empty action, counted as the difference it makes.</summary>
public sealed class UnresolvedBaseCoverageTests
{
    private const string DIRECT = "a direct field access";
    private const string REFERENCE = "an access through a ref reference";
    private const string KNOWN = "a known-call effect";
    private const string UNKNOWN = "an unknown effect";

    private const string UNSEEN = "an object base from code the analysis does not see";
    private const string LOCAL = "a struct copy: a local";
    private const string PARAMETER = "a struct copy: a by-value parameter";
    private const string RESULT = "a struct copy: a call's result";
    private const string DEFENSIVE = "a struct copy: a defensive copy";
    private const string REF_LOCAL = "a struct copy: a ref to a local";
    private const string CONSTRUCTION = "a construction-local base";
    private const string FRESH = "a fresh base";
    private const string RESOLVED = "a resolved base";

    private const string SERIALIZE = "System.Text.Json.JsonSerializer.Serialize";
    private const string WRITE_LINE = "System.Console.WriteLine";

    private static readonly Lazy<int> Empty = new(() => Count("", ""));

    public static TheoryData<string, string, string, string, int> Cases => new()
    {
        { DIRECT, UNSEEN, "unseen.N = 1;", "", 1 },
        { DIRECT, LOCAL, "var local = new S(); local.X = 1;", "", 0 },
        { DIRECT, PARAMETER, "value.X = 1;", "", 0 },
        { DIRECT, RESULT, "_ = Holder.Make().X;", "", 0 },
        { DIRECT, DEFENSIVE, "Holder.Ro.Bump();", "", 0 },
        { DIRECT, REF_LOCAL, "var local = new S(); ref S r = ref local; r.X = 1;", "", 0 },
        { DIRECT, CONSTRUCTION, "_ = new Built();", "N = 1;", 0 },
        { DIRECT, FRESH, "var box = new Box(); box.N = 1;", "", 0 },
        { DIRECT, RESOLVED, "Holder.Shared.N = 1;", "", 0 },
        { DIRECT, UNSEEN, "unseen.S.Hit(1);", "", 2 },
        { DIRECT, LOCAL, "var local = new S(); local.Hit(1);", "", 0 },
        { DIRECT, RESOLVED, "Holder.Shared.S.Hit(1);", "", 0 },

        { REFERENCE, UNSEEN, "ref int r = ref unseen.N; r = 1;", "", 1 },
        { REFERENCE, LOCAL, "var local = new S(); ref int r = ref local.X; r = 1;", "", 0 },
        { REFERENCE, PARAMETER, "ref int r = ref value.X; r = 1;", "", 0 },
        { REFERENCE, RESULT, "_ = Holder.Read(Holder.Make());", "", 0 },
        { REFERENCE, DEFENSIVE, "Holder.Touch(in Holder.Shared.S);", "", 0 },
        { REFERENCE, REF_LOCAL, "int local = 0; Holder.Set(ref local);", "", 0 },
        { REFERENCE, CONSTRUCTION, "_ = new Built();", "ref int r = ref N; r = 1;", 0 },
        { REFERENCE, FRESH, "var box = new Box(); ref int r = ref box.N; r = 1;", "", 0 },
        { REFERENCE, RESOLVED, "ref int r = ref Holder.Shared.N; r = 1;", "", 0 },

        { KNOWN, UNSEEN, $"{SERIALIZE}(unseen);", "", 1 },
        { KNOWN, LOCAL, $"var local = new S(); {SERIALIZE}(local);", "", 0 },
        { KNOWN, PARAMETER, $"{SERIALIZE}(value);", "", 0 },
        { KNOWN, PARAMETER, $"{SERIALIZE}(count);", "", 0 },
        { KNOWN, RESULT, $"{SERIALIZE}(Holder.Make());", "", 0 },
        { KNOWN, CONSTRUCTION, "_ = new Built();", $"{SERIALIZE}(this);", 0 },
        { KNOWN, FRESH, $"var box = new Box(); {SERIALIZE}(box);", "", 0 },
        { KNOWN, RESOLVED, $"{SERIALIZE}(Holder.Shared);", "", 0 },

        { UNKNOWN, UNSEEN, $"{WRITE_LINE}(unseen);", "", 1 },
        { UNKNOWN, LOCAL, $"var local = new S(); {WRITE_LINE}(local);", "", 0 },
        { UNKNOWN, PARAMETER, $"{WRITE_LINE}(value);", "", 0 },
        { UNKNOWN, PARAMETER, $"{WRITE_LINE}(count);", "", 0 },
        { UNKNOWN, RESULT, $"{WRITE_LINE}(Holder.Make());", "", 0 },
        { UNKNOWN, CONSTRUCTION, "_ = new Built();", $"{WRITE_LINE}(this);", 0 },
        { UNKNOWN, FRESH, $"var box = new Box(); {WRITE_LINE}(box);", "", 0 },
        { UNKNOWN, RESOLVED, $"{WRITE_LINE}(Holder.Shared);", "", 0 }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Access_dropped_for_want_of_a_region_is_counted_unless_left_out_on_purpose(string form, string reason, string body,
                                                                                         string constructor, int expected)
    {
        Assert.True(form.Length != 0 && reason.Length != 0);
        Assert.Equal(expected, Count(body, constructor) - Empty.Value);
    }

    [Fact]
    public void Library_returned_reference_into_struct_storage_is_counted()
    {
        Assert.Equal(1, Count("ref S r = ref System.Runtime.CompilerServices.Unsafe.AsRef(in Holder.Shared.S); r.X = 1;", "") - Empty.Value);
    }

    [Fact]
    public void Element_access_on_a_collection_no_field_holds_is_not_counted()
    {
        // The array a helper returns is neither fresh in the action nor under construction there, and no field holds it (TD-043).
        Assert.Equal(0, Count("Holder.Fill(Holder.MakeArray());", "") - Empty.Value);
    }

    [Fact]
    public void Immutable_object_from_unseen_code_is_not_counted_where_an_effect_touches_nothing_of_it()
    {
        Assert.Equal(0, Count($"{SERIALIZE}(text); {WRITE_LINE}(text);", "") - Empty.Value);
    }

    /// <summary>The unresolved-base counter of a run of one action whose body is <paramref name="body"/>, with
    /// <paramref name="constructor"/> as the body of <c>Built</c>'s constructor.</summary>
    /// <param name="body">The action's body.</param>
    /// <param name="constructor">The body of <c>Built</c>'s constructor.</param>
    private static int Count(string body, string constructor) => Analyze($$"""
        public sealed class Item { public int N; }

        public struct S
        {
            public int X;
            public Item Ref;
            public int[] Cells;
            public void Bump() { X++; }
            public void Hit(int i) { Cells[i] = 1; }
        }

        public sealed class Box { public int N; public S S; }

        public sealed class Built
        {
            public int N;
            public Built() { {{constructor}} }
        }

        public static class Holder
        {
            public static Box Shared = new Box();
            public static readonly S Ro;
            public static S Make() => new S();
            public static int[] MakeArray() => new int[4];
            public static int Read(in S s) => s.X;
            public static void Touch(in S s) { s.Bump(); }
            public static void Set(ref int target) { target = 1; }
            public static void Fill(int[] array) { array[0] = 1; }
        }

        public class CaseController : ControllerBase
        {
            public void Post(Box unseen, S value, int count, string text)
            {
                {{body}}
            }
        }
        """ + Startup()).Counter(CoverageCounters.UNRESOLVED_BASE);
}
