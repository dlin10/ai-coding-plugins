using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class ReferenceAccessTests
{
    [Fact]
    public void Ref_local_write_reaches_its_element()
    {
        var run = AnalyzeCase("ref int r = ref _state.Slots[0]; r = 1;", "_state.Slots[0] = 2;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Ref_returning_indexer_write_reaches_returned_field()
    {
        var run = AnalyzeCase("_state[0] = 1;", "_state.Cell = 2;",
                              "public int Cell; public ref int this[int index] => ref Cell;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell");
    }

    [Fact]
    public void Out_field_with_source_writes_at_the_callee_site()
    {
        var run = AnalyzeCase("_state.Assign(out _state.Cell);", "_state.Cell = 2;",
                              "public int Cell; public void Assign(out int value) => value = 1;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell" &&
                                             pair.First.Source.StartLine != pair.Second.Source.StartLine);
    }

    [Fact]
    public void Out_element_with_source_writes_its_cell()
    {
        var run = AnalyzeCase("_state.Assign(out _state.Slots[0]);", "_state.Slots[0] = 2;",
                              "public void Assign(out int value) => value = 1;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Ref_field_with_source_is_read_modify_write()
    {
        var run = AnalyzeCase("_state.Increment(ref _state.Cell);", "_state.Cell = 2;",
                              "public int Cell; public void Increment(ref int value) => value++;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell" &&
                                             (pair.First.Operation == ConcurrencyHunter.Analysis.AccessOperation.ReadModifyWrite ||
                                              pair.Second.Operation == ConcurrencyHunter.Analysis.AccessOperation.ReadModifyWrite));
    }

    [Fact]
    public void Ref_element_with_source_is_read_modify_write()
    {
        var run = AnalyzeCase("_state.Increment(ref _state.Slots[0]);", "_state.Slots[0] = 2;",
                              "public void Increment(ref int value) => value++;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]" &&
                                             (pair.First.Operation == ConcurrencyHunter.Analysis.AccessOperation.ReadModifyWrite ||
                                              pair.Second.Operation == ConcurrencyHunter.Analysis.AccessOperation.ReadModifyWrite));
    }

    [Fact]
    public void In_field_with_source_is_read()
    {
        var run = AnalyzeCase("_ = _state.Read(in _state.Cell);", "_state.Cell = 2;",
                              "public int Cell; public int Read(in int value) => value;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell" &&
                                             (pair.First.Operation == ConcurrencyHunter.Analysis.AccessOperation.Read ||
                                              pair.Second.Operation == ConcurrencyHunter.Analysis.AccessOperation.Read));
    }

    [Fact]
    public void In_element_with_source_is_read()
    {
        var run = AnalyzeCase("_ = _state.Read(in _state.Slots[0]);", "_state.Slots[0] = 2;",
                              "public int Read(in int value) => value;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]" &&
                                             (pair.First.Operation == ConcurrencyHunter.Analysis.AccessOperation.Read ||
                                              pair.Second.Operation == ConcurrencyHunter.Analysis.AccessOperation.Read));
    }

    [Fact]
    public void Out_assignment_under_callee_lock_is_protected()
    {
        var run = AnalyzeCase("_state.Assign(out _state.Cell);", "lock (_state.Gate) _state.Cell = 2;",
                              "public int Cell; public readonly object Gate = new(); public void Assign(out int value) { lock (Gate) value = 1; }");

        Assert.Equal(2, run.Collection.Accesses.Count(access => access.Resource.Member.Name == "Cell" &&
                                                        !access.IsConstructionLocal));
        Assert.DoesNotContain(run.Pairs.Pairs, pair => pair.Resource.Member.Name == "Cell");
    }

    [Fact]
    public void Out_field_without_source_writes_at_call_site()
    {
        var run = AnalyzeCase("int.TryParse(\"1\", out _state.Cell);", "_state.Cell = 2;", "public int Cell;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell");
    }

    [Fact]
    public void Reassigned_ref_local_writes_only_its_new_target()
    {
        var run = AnalyzeCase("ref int r = ref _state.Slots[0]; r = ref _state.Slots[1]; r = 1;",
                              "_state.Slots[1] = 2;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[1]");
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                              access.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Taking_a_ref_local_address_does_not_read_or_write_its_element()
    {
        var run = AnalyzeCase("ref int r = ref _state.Slots[0];", "_state.Slots[0] = 2;");

        Assert.DoesNotContain(run.Collection.Accesses, access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                              access.Resource.Selector is not null);
    }

    [Fact]
    public void Returning_a_reference_without_dereference_is_not_an_access()
    {
        var run = AnalyzeCase("ref int r = ref _state.Get();", "_state.Slots[0] = 2;",
                              "public ref int Get() => ref Slots[0];");

        Assert.DoesNotContain(run.Collection.Accesses, access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                              access.Resource.Selector is not null);
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Symbol.Contains("State.Get", StringComparison.Ordinal) &&
                                                              access.Resource.Selector is not null);
    }

    [Fact]
    public void Ref_return_write_reaches_its_element()
    {
        var run = AnalyzeCase("_state.Get() = 1;", "_state.Slots[0] = 2;",
                              "public ref int Get() => ref Slots[0];");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Ref_return_read_reaches_its_element()
    {
        var run = AnalyzeCase("_ = _state.Get();", "_state.Slots[0] = 2;",
                              "public ref int Get() => ref Slots[0];");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Ref_returning_indexer_read_reaches_returned_field()
    {
        var run = AnalyzeCase("_ = _state[0];", "_state.Cell = 2;",
                              "public int Cell; public ref int this[int index] => ref Cell;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell");
    }

    [Fact]
    public void Ref_local_read_reaches_its_element()
    {
        var run = AnalyzeCase("ref int r = ref _state.Slots[0]; _ = r;", "_state.Slots[0] = 2;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Conditional_ref_return_reaches_each_proven_target()
    {
        var run = AnalyzeCase("_state.Choose(_state.Flag) = 1;", "_state.Slots[0] = 2; _state.Slots[1] = 3;",
                              "public bool Flag; public ref int Choose(bool flag) { if (flag) return ref Slots[0]; return ref Slots[1]; }");

        Assert.Equal(["[0]", "[1]"], run.Pairs.Pairs.Where(pair => AcrossWorkers(pair) && pair.Resource.Selector is not null)
                                               .Select(pair => pair.Resource.Selector!.Text).Distinct().Order(StringComparer.Ordinal));
        foreach (var selector in new[] { "[0]", "[1]" })
            Assert.Contains(run.Collection.Accesses, access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                            access.Resource.Selector?.Text == selector &&
                                                            access.Operation == ConcurrencyHunter.Analysis.AccessOperation.Write);
    }

    [Fact]
    public void Conditional_ref_local_reaches_each_proven_target()
    {
        var run = AnalyzeCase("ref int r = ref (_state.Flag ? ref _state.Slots[0] : ref _state.Slots[1]); r = 1;",
                              "_state.Slots[0] = 2; _state.Slots[1] = 3;", "public bool Flag;");

        Assert.Equal(["[0]", "[1]"], run.Pairs.Pairs.Where(pair => AcrossWorkers(pair) && pair.Resource.Selector is not null)
                                               .Select(pair => pair.Resource.Selector!.Text).Distinct().Order(StringComparer.Ordinal));
        foreach (var selector in new[] { "[0]", "[1]" })
            Assert.Contains(run.Collection.Accesses, access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                            access.Resource.Selector?.Text == selector &&
                                                            access.Operation == ConcurrencyHunter.Analysis.AccessOperation.Write);
    }

    [Fact]
    public void Ref_readonly_indexer_reads_the_returned_cell()
    {
        var run = AnalyzeCase("_ = _state[0];", "_state.Cell = 2;",
                              "public int Cell; public ref readonly int this[int index] => ref Cell;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell");
    }

    [Fact]
    public void Ref_indexer_can_return_a_static_field()
    {
        var run = AnalyzeCase("_state[0] = 1;", "State.Global = 2;",
                              "public static int Global; public ref int this[int index] => ref Global;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Global");
    }

    [Fact]
    public void Ref_indexer_uses_the_region_of_its_other_object()
    {
        var run = AnalyzeCase("_state[0] = 1;", "_state.Node.Cell = 2;",
                              "public Other Node = new(); public ref int this[int index] => ref Node.Cell; public sealed class Other { public int Cell; }");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell" &&
                                             pair.Resource.RegionId != run.Collection.Accesses.First(access =>
                                                 access.Resource.Member.Name == "Node").Resource.RegionId);
    }

    [Fact]
    public void Unrecognized_slice_write_reaches_its_proven_storage()
    {
        var run = AnalyzeCase("_state.WindowData.Slice(1)[0] = 1;", "_state.WindowData[0] = 2;", WindowMembers);

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Data");
        Assert.Contains(run.Collection.Accesses, access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Unrecognized_slice_read_reaches_its_proven_storage()
    {
        var run = AnalyzeCase("_ = _state.WindowData.Slice(1)[0];", "_state.WindowData[0] = 2;", WindowMembers);

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Data");
        Assert.Contains(run.Collection.Accesses, access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Unrecognized_slice_uses_the_storage_its_body_constructs()
    {
        var run = AnalyzeCase("_state.WindowData.Slice(1)[0] = 1;", "_state.OtherData[0] = 2;",
                              "public int[] OtherData = new int[8]; public Window WindowData; " +
                              "public State() { WindowData = new Window(new int[8], OtherData); } " +
                              "public sealed class Window { public int[] Data; private int[] _other; " +
                              "public Window(int[] data, int[] other) { Data = data; _other = other; } " +
                              "public Window Slice(int start) => new(_other, _other); " +
                              "public ref int this[int index] => ref Data[index]; }");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector is not null);
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                              access.Resource.Member.Name == "OtherData" && access.Resource.Selector is not null);
    }

    [Fact]
    public void Out_element_without_source_writes_at_call_site()
    {
        var run = AnalyzeCase("int.TryParse(\"1\", out _state.Slots[0]);", "_state.Slots[0] = 2;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Ref_indexer_with_proven_index_names_its_exact_cell()
    {
        var run = AnalyzeCase("_state[3] = 1;", "_state.Slots[4] = 2;",
                              "public ref int this[int index] => ref Slots[index];");

        Assert.Contains(run.Collection.Accesses, access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Selector?.Text == "[3]");
        Assert.DoesNotContain(run.Pairs.Pairs, pair => pair.Resource.Selector is not null);
    }

    [Fact]
    public void Opaque_ref_return_counts_an_unproven_location_without_guessing()
    {
        var run = AnalyzeCase("System.Runtime.CompilerServices.Unsafe.AsRef(in _state.Cell) = 1;", "_state.Cell = 2;",
                              "public int Cell;");

        Assert.True(run.Counter(ConcurrencyHunter.Accesses.CoverageCounters.UNPROVEN_REFERENCE) > 0);
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                              access.Resource.Member.Name == "Cell" &&
                                                              access.Operation == ConcurrencyHunter.Analysis.AccessOperation.Write);
    }

    [Fact]
    public void Opaque_ref_argument_counts_an_unproven_location_without_guessing()
    {
        var run = AnalyzeCase("System.Array.Resize(ref _state.Slots, 8);", "_state.Slots[0] = 2;");

        Assert.True(run.Counter(ConcurrencyHunter.Accesses.CoverageCounters.UNPROVEN_REFERENCE) > 0);
        Assert.DoesNotContain(run.Collection.Accesses, access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                              access.Resource.Member.Name == "Slots" &&
                                                              access.Operation == ConcurrencyHunter.Analysis.AccessOperation.Write);
    }

    [Fact]
    public void Interlocked_ref_call_keeps_its_atomic_access()
    {
        var run = AnalyzeCase("Interlocked.Increment(ref _state.Cell);", "_state.Cell = 2;", "public int Cell;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell" &&
                                             (pair.First.Operation == ConcurrencyHunter.Analysis.AccessOperation.AtomicReadModifyWrite ||
                                              pair.Second.Operation == ConcurrencyHunter.Analysis.AccessOperation.AtomicReadModifyWrite));
    }

    [Fact]
    public void Ref_to_a_cell_of_a_parameter_array_reaches_the_callers_field()
    {
        var run = AnalyzeCase("State.Write(_state.Slots);", "_state.Slots[0] = 2;",
                              "public static void Write(int[] array) { ref int r = ref array[0]; r = 1; }");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Direct_write_to_a_cell_of_a_parameter_array_reaches_the_callers_field()
    {
        var run = AnalyzeCase("State.Write(_state.Slots);", "_state.Slots[0] = 2;",
                              "public static void Write(int[] array) { array[0] = 1; }");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Direct_read_of_a_cell_of_a_parameter_array_crosses_two_calls()
    {
        var run = AnalyzeCase("_ = State.Outer(_state.Slots);", "_state.Slots[0] = 2;",
                              "public static int Outer(int[] array) => Read(array); " +
                              "public static int Read(int[] array) => array[0];");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Direct_write_to_a_local_array_handed_to_a_callee_is_no_resource()
    {
        var run = AnalyzeCase("var local = new int[8]; State.Write(local);", "_state.Slots[0] = 2;",
                              "public static void Write(int[] array) { array[0] = 1; }");

        Assert.Empty(run.Pairs.Pairs);
        Assert.Equal(0, run.Counter(ConcurrencyHunter.Accesses.CoverageCounters.UNPROVEN_REFERENCE));
    }

    [Fact]
    public void Ref_to_a_cell_of_a_parameter_array_crosses_two_calls()
    {
        var run = AnalyzeCase("State.Outer(_state.Slots);", "_state.Slots[0] = 2;",
                              "public static void Outer(int[] array) => Write(array); " +
                              "public static void Write(int[] array) { ref int r = ref array[0]; r = 1; }");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Ref_to_a_cell_of_a_parameter_span_keeps_the_callers_slice()
    {
        var run = AnalyzeCase("State.Write(_state.Slots.AsSpan(4));", "_state.Slots[0] = 2;",
                              "public static void Write(Span<int> span) { ref int r = ref span[0]; r = 1; }");

        Assert.Contains(run.Collection.Accesses, access => access.Symbol.Contains("Write", StringComparison.Ordinal) &&
                                                        access.Resource.Member.Name == "Slots" &&
                                                        access.Resource.Selector?.Text == "[4]");
        Assert.DoesNotContain(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector is not null);
    }

    [Fact]
    public void Ref_read_modify_write_needs_a_section_over_its_read()
    {
        var run = AnalyzeCase("ref int r = ref _state.Slots[0]; r += Convert.ToInt32(_state.Gate.WaitOne()); _state.Gate.ReleaseMutex();",
                              "_state.Gate.WaitOne(); _state.Slots[0] = 2; _state.Gate.ReleaseMutex();",
                              "public readonly Mutex Gate = new();");

        var pair = Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
        var first = pair.First.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) ? pair.First : pair.Second;
        Assert.Equal(ConcurrencyHunter.Analysis.AccessOperation.ReadModifyWrite, first.Operation);
        Assert.Empty(first.HeldProtection);
    }

    [Fact]
    public void Ref_read_modify_write_under_one_section_stays_protected()
    {
        var run = AnalyzeCase("ref int r = ref _state.Slots[0]; _state.Gate.WaitOne(); r += 1; _state.Gate.ReleaseMutex();",
                              "_state.Gate.WaitOne(); _state.Slots[0] = 2; _state.Gate.ReleaseMutex();",
                              "public readonly Mutex Gate = new();");

        Assert.DoesNotContain(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
    }

    [Fact]
    public void Conditional_ref_with_an_unproven_alternative_counts_it()
    {
        var run = AnalyzeCase("ref int r = ref (_state.Flag ? ref _state.Slots[0] : " +
                              "ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(_state.Other)); r = 1;",
                              "_state.Slots[0] = 2;", "public bool Flag; public int[] Other = new int[8];");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
        Assert.Equal(1, run.Counter(ConcurrencyHunter.Accesses.CoverageCounters.UNPROVEN_REFERENCE));
    }

    [Fact]
    public void Parameter_array_a_caller_cannot_name_counts_an_unproven_location()
    {
        var run = AnalyzeCase("State.Write(_state.Slots); State.Write(new int[8]);", "_state.Slots[0] = 2;",
                              "public static void Write(int[] array) { ref int r = ref array[0]; r = 1; }");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Selector?.Text == "[0]");
        Assert.Equal(1, run.Counter(ConcurrencyHunter.Accesses.CoverageCounters.UNPROVEN_REFERENCE));
    }

    [Fact]
    public void Read_folded_into_a_ref_read_modify_write_is_not_unproven()
    {
        var run = AnalyzeCase("ref int r = ref _state.Slots[0]; r += 1;", "_state.Slots[0] = 2;");

        Assert.Equal(0, run.Counter(ConcurrencyHunter.Accesses.CoverageCounters.UNPROVEN_REFERENCE));
    }

    [Fact]
    public void Read_of_a_ref_parameter_after_writing_it_is_a_read_where_it_stands()
    {
        var run = AnalyzeCase("_ = _state.Touch(ref _state.Cell);", "lock (_state.Gate) { _state.Cell = 2; }",
                              "public int Cell; public readonly object Gate = new(); " +
                              "public int Touch(ref int value) { lock (Gate) { value = 1; } return value; }");

        Assert.Contains(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell" &&
                                               new[] { pair.First, pair.Second }.Any(side => side.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                                                              side.Operation == ConcurrencyHunter.Analysis.AccessOperation.Read &&
                                                                                              side.HeldProtection.Count == 0));
    }

    [Fact]
    public void Out_field_of_an_extern_method_writes_at_call_site()
    {
        var run = AnalyzeCase("State.Fill(out _state.Cell);", "_state.Cell = 2;",
                              "public int Cell; [System.Runtime.InteropServices.DllImport(\"native\")] public static extern void Fill(out int value);");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell");
    }

    [Fact]
    public void Write_to_a_cell_of_a_collection_a_call_returns_reaches_its_field()
    {
        var run = AnalyzeCase("_state.Window()[0] = 1;", "_state.Data[0] = 2;",
                              "public int[] Data = new int[8]; public int[] Window() => Data;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Data");
        Assert.Contains(run.Collection.Accesses, access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
                                                        access.Resource.Member.Name == "Data" &&
                                                        access.Resource.Selector == ConcurrencyHunter.Analysis.ElementSelector.Unknown);
    }

    [Fact]
    public void Read_of_a_cell_of_a_parameter_a_call_hands_back_reaches_the_callers_field()
    {
        var run = AnalyzeCase("_ = State.Pass(_state.Slots)[0];", "_state.Slots[0] = 2;",
                              "public static int[] Pass(int[] array) => array;");

        Assert.Single(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Slots");
    }

    [Fact]
    public void Cell_of_a_fresh_collection_a_call_returns_is_no_resource()
    {
        var run = AnalyzeCase("_state.Fresh()[0] = 1;", "_state.Slots[0] = 2;", "public int[] Fresh() => new int[8];");

        Assert.Empty(run.Pairs.Pairs);
        Assert.Equal(0, run.Counter(ConcurrencyHunter.Accesses.CoverageCounters.UNPROVEN_REFERENCE));
    }

    private const string WindowMembers ="public Window WindowData = new(new int[8]); " +
                                         "public sealed class Window { public int[] Data; " +
                                         "public Window(int[] data) { Data = data; } " +
                                         "public Window Slice(int start) => new(Data); " +
                                         "public ref int this[int index] => ref Data[index]; }";

    private static bool AcrossWorkers(ConcurrencyHunter.Accesses.AccessPair pair) =>
        pair.First.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
        pair.Second.Root.Symbol.Contains("SecondWorker", StringComparison.Ordinal) ||
        pair.Second.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
        pair.First.Root.Symbol.Contains("SecondWorker", StringComparison.Ordinal);

    private static EngineRun AnalyzeCase(string first, string second, string stateMembers = "") => Analyze(Usings + $$"""
        public sealed class State
        {
            public int[] Slots = new int[8];
            {{stateMembers}}
        }

        public sealed class FirstWorker : BackgroundService
        {
            private readonly State _state;
            public FirstWorker(State state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{first}}
                return Task.CompletedTask;
            }
        }

        public sealed class SecondWorker : BackgroundService
        {
            private readonly State _state;
            public SecondWorker(State state) => _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{second}}
                return Task.CompletedTask;
            }
        }

        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();"));
}
