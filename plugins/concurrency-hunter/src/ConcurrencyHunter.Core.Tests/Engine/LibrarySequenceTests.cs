using System.Text;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A known call whose result is <c>sequence(…)</c> returns a library sequence: nothing of it runs at the call, and whoever
/// enumerates it — a <c>foreach</c>, a copy, a known call reading it deep or naming its elements, the enumeration of another sequence
/// built from it — runs its delegates, enumerates its sources and makes its argument effects there, in its own execution; one that
/// escapes as an iterator does is enumerated by an unknown execution as well (R5). The same consumers enumerate a user iterator where
/// they stand. The models are project models of a library the run has no source of.</summary>
public sealed class LibrarySequenceTests
{
    private const string PRIMARY = "alloc:State..ctor()#Primary";
    private const string SECONDARY = "alloc:State..ctor()#Secondary";
    private const string KEYED = "alloc:State..ctor()#Keyed";
    private const string COUNTING = "Seqs.Lib.Filter(_state.Items, item => { _state.Count = 1; return true; })";

    [Fact]
    public void Sequence_copied_by_a_list_constructor_is_enumerated_at_the_call()
    {
        var run = Run($"var copy = new List<Item>({COUNTING});");

        AssertRunsOnlyInTheWorker(run, "Count");
        AssertNoUnknownEnumeration(run);
    }

    [Fact]
    public void Sequence_captured_by_a_stored_delegate_is_also_enumerated_by_an_unknown_execution()
    {
        var run = Run($"var sequence = {COUNTING};\n_state.Stored = () => {{ foreach (var item in sequence) {{ }} return 0; }};");

        Assert.Contains(Worker(run, "Count"), access => KindOf(run, access) == ExecutionKind.UnknownEnumeration);
    }

    [Fact]
    public void Sequence_with_an_explicit_GetEnumerator_runs_its_delegate_at_MoveNext()
    {
        var run = Run($"var sequence = {COUNTING};\nusing var enumerator = sequence.GetEnumerator();\nenumerator.MoveNext();");

        AssertRunsOnlyInTheWorker(run, "Count");
        AssertNoUnknownEnumeration(run);
    }

    [Fact]
    public void Sequence_delegate_runs_where_the_sequence_is_enumerated()
    {
        var run = Run($"var sequence = {COUNTING};\nforeach (var item in sequence) {{ }}");

        AssertRunsOnlyInTheWorker(run, "Count");
        AssertNoUnknownEnumeration(run);
    }

    [Fact]
    public void Sequence_delegate_does_not_run_at_the_call()
    {
        var run = Run($"var sequence = {COUNTING};");

        Assert.Empty(run.Run.Of("Count"));
        AssertNoUnknownEnumeration(run);
        Assert.Single(Heap(run).LibrarySequences.Values, sequence => !sequence.IsGrouping);
    }

    [Fact]
    public void Sequence_enumerated_through_IOrderedEnumerable_runs_its_delegate()
    {
        var run = Run("IOrderedEnumerable<Item> ordered = Seqs.Lib.Order(_state.Items, item => { _state.Count = 1; return 0; });\n" +
                      "foreach (var item in ordered) { }");

        AssertRunsOnlyInTheWorker(run, "Count");
    }

    [Fact]
    public void Sequence_built_on_an_ordered_sequence_runs_both_key_selectors()
    {
        var run = Run("var ordered = Seqs.Lib.Then(Seqs.Lib.Order(_state.Items, item => { _state.Count = 1; return 0; }), item => { _state.Tally = 1; return 0; });\n" +
                      "foreach (var item in ordered) { }");

        AssertRunsOnlyInTheWorker(run, "Count");
        AssertRunsOnlyInTheWorker(run, "Tally");
    }

    [Fact]
    public void Iterator_fate_with_an_unresolved_delegate_is_a_dispatch_at_each_enumeration()
    {
        var run = Run("var sequence = Seqs.Lib.Filter(_state.Items, Seqs.Lib.Unknown<Item>());\n" +
                      "foreach (var item in sequence) { }\n" +
                      "foreach (var other in sequence) { }");

        Assert.Equal(2, Heap(run).UnresolvedDispatches.Count(dispatch => IsWorker(run, dispatch.Instance)));
        Assert.Contains(Worker(run, "Hits"), access => access.Operation == AccessOperation.UnknownEffect && access.Resource.Region == PRIMARY &&
                                                       access.Source.StartLine == Line(run.Text, "foreach (var other in sequence)"));
    }

    [Fact]
    public void Argument_named_twice_is_enumerated_once()
    {
        // A model naming the elements of its source twice beside its deep read makes the same accesses as one naming them once.
        var twice = Run("foreach (var item in Seqs.Lib.Twice(_state.Items)) { }");
        var once = Run("foreach (var item in Seqs.Lib.Once(_state.Items)) { }");

        var accesses = AtLine(twice, "Items", "foreach (var item in Seqs.Lib.Twice");
        Assert.NotEmpty(accesses);
        Assert.All(accesses, access => Assert.Equal(AccessOperation.Read, access.Operation));
        Assert.Equal(AtLine(once, "Items", "foreach (var item in Seqs.Lib.Once").Count, accesses.Count);
        Assert.Equal(accesses.Count, accesses.Select(access => (access.Resource.Identity, access.Operation)).Distinct().Count());
    }

    [Fact]
    public void Sequence_enumerated_by_two_requests_runs_in_both()
    {
        var run = Run("_state.Kept = Seqs.Lib.Filter(_state.Items, item => { _state.Count++; return true; });\n" +
                      "foreach (var item in _state.Kept) { }",
                      action: "foreach (var item in _state.Kept!) { }");

        var roots = run.Run.Of("Count").Where(access => KindOf(run, access) == ExecutionKind.Root).Select(access => access.ExecutionId).Distinct();
        Assert.Equal(2, roots.Count());
    }

    [Fact]
    public void Sequence_created_under_a_lock_runs_its_delegate_outside_it()
    {
        var run = Run("IEnumerable<Item> sequence;\n" +
                      $"lock (_state.Gate) {{ sequence = {COUNTING}; }}\n" +
                      "foreach (var item in sequence) { }",
                      other: "lock (_state.Gate) { _state.Count = 2; }");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.Root, KindOf(run, write));
        Assert.Empty(write.HeldProtection);
        Assert.NotEmpty(run.Run.PairsOn("Count"));
    }

    [Fact]
    public void Sequence_enumerated_under_a_lock_runs_its_delegate_under_it()
    {
        var run = Run($"var sequence = {COUNTING};\n" +
                      "lock (_state.Gate) { foreach (var item in sequence) { } }",
                      other: "lock (_state.Gate) { _state.Count = 2; }");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.NotEmpty(write.HeldProtection);
        Assert.Empty(run.Run.PairsOn("Count"));
    }

    [Fact]
    public void Sequence_stored_in_a_field_is_also_enumerated_by_an_unknown_execution()
    {
        var run = Run($"_state.Kept = {COUNTING};");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.UnknownEnumeration, KindOf(run, write));
        Assert.DoesNotContain(run.Run.Collection.Coverage.Gaps, gap => gap.Callee.StartsWith("Seqs.", StringComparison.Ordinal));
    }

    [Fact]
    public void Sequence_handed_to_an_opaque_call_is_also_enumerated_by_an_unknown_execution()
    {
        var run = Run($"Seqs.Lib.Keep({COUNTING});");

        var write = Assert.Single(Worker(run, "Count"));
        Assert.Equal(ExecutionKind.UnknownEnumeration, KindOf(run, write));
    }

    [Fact]
    public void Sequence_created_in_startup_and_escaped_meets_a_startup_write()
    {
        // The library may enumerate what it holds as soon as it has it, while startup is still running (issue #123).
        var run = Run("", startup: "Totals.Escaped = Seqs.Lib.Filter(new List<int> { 1 }, value => { Totals.Sum = 1; return true; }); Totals.Sum = 2;");

        Assert.Contains(run.Run.PairsOn("Sum"), pair => new[] { pair.First, pair.Second }.Any(access => access.ExecutionId == ExecutionModel.STARTUP) &&
                                                        new[] { pair.First, pair.Second }.Any(access => KindOf(run, access) == ExecutionKind.UnknownEnumeration));
    }

    [Fact]
    public void Sequence_read_deep_by_a_known_call_is_enumerated_at_the_call()
    {
        var run = Run($"Seqs.Lib.Read({COUNTING});");

        AssertRunsOnlyInTheWorker(run, "Count");
        AssertNoUnknownEnumeration(run);
        // What it yields is read deep there too.
        Assert.Contains(Worker(run, "Hits"), access => access.Operation == AccessOperation.Read && access.Resource.Region == PRIMARY &&
                                                       access.Source.StartLine == Line(run.Text, "Seqs.Lib.Read("));
    }

    [Fact]
    public void Sequence_copied_by_AddRange_is_enumerated_at_the_call()
    {
        var run = Run($"var list = new List<Item>();\nlist.AddRange({COUNTING});");

        AssertRunsOnlyInTheWorker(run, "Count");
        AssertNoUnknownEnumeration(run);
    }

    [Fact]
    public void Sequence_named_by_an_invoke_now_input_is_enumerated_at_the_call()
    {
        var run = Run($"Seqs.Lib.Each({COUNTING}, item => item.Hits = 1);");

        AssertRunsOnlyInTheWorker(run, "Count");
        Assert.Equal([PRIMARY], Written(run, "item => item.Hits = 1"));
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("delegate return")]
    [InlineData("both moments")]
    public void Invoke_now_inputs_are_enumerated_at_the_call_even_with_a_sequence_result(string origin)
    {
        var fromReturn = origin == "delegate return";
        var result = origin == "both moments" ? "sequence(elements(arg:source))" : "sequence(arg:other)";
        var decision = fromReturn
            ? """ "effects":{},"result":"sequence(arg:other)","fates":{"make":{"fate":"invoke-now","inputs":[]},"action":{"fate":"invoke-now","inputs":[["elements(returns:make)"]]}} """
            : "\"effects\":{},\"result\":\"" + result + "\",\"fates\":{\"action\":{\"fate\":\"invoke-now\",\"inputs\":[[\"elements(arg:source)\"]]}}";
        var name = fromReturn ? "ImmediateMake" : "Immediate";
        var id = DocumentationCommentId.CreateDeclarationId(LibrarySource.Value.GetTypeByMetadataName("Seqs.Lib")!.GetMembers(name).Single())!;
        var model = "{\"schemaVersion\":1,\"assemblies\":[\"Seqs\"],\"models\":[{\"member\":\"" + id + "\"," + decision + "}]}";
        var work = fromReturn
            ? "var sequence = Seqs.Lib.ImmediateMake(() => _state.Produce(), _state.Second, item => { });"
            : "var sequence = Seqs.Lib.Immediate(_state.Produce(), _state.Second, item => { });";
        if (origin == "both moments")
            work += " Later(sequence);";
        var text = Usings + Source(work, "", "", "");
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [Library.Value] }, ("Case.cs", text));
        var run = new Case(AnalyzeScope(solution, "scope:Fixture", Models(solution, model)), text);
        var writes = run.Run.Of("Count").Where(access => access.Operation == AccessOperation.Write).ToArray();

        Assert.NotEmpty(writes);
        Assert.All(writes, access => Assert.Equal(ExecutionKind.Root, KindOf(run, access)));
        Assert.Contains(writes, access => !access.CallPath.Any(member => member.Contains("Worker.Later", StringComparison.Ordinal)));
        if (origin == "both moments")
        {
            var enumerations = Heap(run).ExecutionEdges.Where(edge => edge.Reason == "iterator-enumeration").ToArray();
            Assert.Contains(enumerations, edge => Heap(run).Instances[edge.CallerInstance].BodyId.Contains("Worker.ExecuteAsync", StringComparison.Ordinal));
            Assert.Contains(enumerations, edge => Heap(run).Instances[edge.CallerInstance].BodyId.Contains("Worker.Later", StringComparison.Ordinal));
        }
        AssertNoUnknownEnumeration(run);
    }

    [Fact]
    public void Sequence_of_a_sequence_enumerates_its_source()
    {
        var run = Run("var outer = Seqs.Lib.Filter(Seqs.Lib.Filter(_state.Items, item => { _state.Count = 1; return true; }), item => { _state.Tally = 1; return true; });\n" +
                      "foreach (var item in outer) { }");

        AssertRunsOnlyInTheWorker(run, "Count");
        AssertRunsOnlyInTheWorker(run, "Tally");
    }

    [Fact]
    public void Foreach_over_a_sequence_yields_its_sources_elements()
    {
        var run = Run("foreach (var item in Seqs.Lib.Filter(_state.Items, item => true)) item.Hits = 1;");

        Assert.Equal([PRIMARY], Written(run, "item.Hits = 1"));
    }

    [Fact]
    public void Foreach_over_a_selector_sequence_yields_what_the_selector_returned()
    {
        var run = Run("foreach (var item in Seqs.Lib.Map(_state.Items, item => (Item)_state.Second)) item.Hits = 1;");

        Assert.Equal([SECONDARY], Written(run, "item.Hits = 1"));
    }

    [Fact]
    public void Sequence_of_the_elements_of_a_delegates_return()
    {
        var run = Run("foreach (var item in Seqs.Lib.Flatten(_state.Items, item => _state.Others)) item.Hits = 1;");

        Assert.Equal([SECONDARY], Written(run, "item.Hits = 1"));
    }

    [Fact]
    public void Grouping_Key_is_what_the_key_selector_returned()
    {
        var run = Run("foreach (var group in Seqs.Lib.Group(_state.Items, item => (Item)_state.Key)) group.Key.Hits = 1;");

        Assert.Equal([KEYED], Written(run, "group.Key.Hits = 1"));
    }

    [Fact]
    public void Grouping_enumerated_through_IGrouping_yields_its_elements()
    {
        var run = Run("foreach (var group in Seqs.Lib.Group(_state.Items, item => (Item)_state.Key)) foreach (var item in group) item.Hits = 1;");

        Assert.Equal([PRIMARY], Written(run, "item.Hits = 1"));
    }

    [Fact]
    public void Argument_effects_of_a_lazy_member_happen_at_the_enumeration()
    {
        var run = Run("var sequence = Seqs.Lib.Lazy(_state.Items, _state.Second);\nforeach (var item in sequence) { }");

        Assert.Empty(Written(run, "Seqs.Lib.Lazy("));
        Assert.Equal([SECONDARY], Written(run, "foreach (var item in sequence)"));
    }

    [Fact]
    public void Write_argument_on_a_sequence_writes_what_it_yields()
    {
        var run = Run("Seqs.Lib.Mark(Seqs.Lib.Filter(_state.Items, item => true));");

        Assert.Equal([PRIMARY], Written(run, "Seqs.Lib.Mark("));
        Assert.DoesNotContain(Worker(run, "Hits").Concat(run.Run.Collection.Accesses),
                              access => access.Resource.Region?.StartsWith("sequence:", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Iterator_copied_into_a_list_is_enumerated_in_the_caller()
    {
        var run = Run("var copy = new List<Item>(_state.Produce());");

        var write = Assert.Single(run.Run.Of("Count"));
        Assert.Equal(ExecutionKind.Root, KindOf(run, write));
        AssertNoUnknownEnumeration(run);
    }

    [Fact]
    public void Iterator_handed_to_an_opaque_call_still_gets_an_unknown_enumeration()
    {
        var run = Run("Seqs.Lib.Keep(_state.Produce());");

        Assert.Contains(run.Run.Of("Count"), access => KindOf(run, access) == ExecutionKind.UnknownEnumeration);
    }

    // ---- what a value names the elements of (review F-0057 to F-0061) ----

    [Fact]
    public void Elements_of_what_an_invoke_now_delegate_returned_are_enumerated_at_the_call()
    {
        var run = Run("var made = Seqs.Lib.Collect(() => _state.Produce());\nSeqs.Lib.Collect(() => _state.Others)[0].Hits = 1;");

        AssertIteratorRunsOnlyInTheWorker(run);
        AssertNoUnknownEnumeration(run);
        Assert.Equal([SECONDARY], Written(run, "[0].Hits = 1"));
    }

    [Fact]
    public void Sequence_a_value_builds_is_not_enumerated_at_the_call()
    {
        var run = Run("var wrapped = Seqs.Lib.Wrap(_state.Produce());");

        Assert.Empty(run.Run.Of("Count"));
    }

    [Fact]
    public void Sequence_a_value_builds_enumerates_its_source_where_it_is_enumerated()
    {
        var run = Run("var wrapped = Seqs.Lib.Wrap(_state.Produce());\nforeach (var item in wrapped) { }");

        AssertIteratorRunsOnlyInTheWorker(run);
        AssertNoUnknownEnumeration(run);
    }

    [Fact]
    public void Sequence_of_what_a_selector_returned_enumerates_it_where_it_is_enumerated()
    {
        var run = Run("foreach (var item in Seqs.Lib.Flatten(_state.Items, item => _state.Produce())) { }");

        AssertIteratorRunsOnlyInTheWorker(run);
        AssertNoUnknownEnumeration(run);
    }

    [Fact]
    public void Sequence_of_what_a_selector_returned_reads_its_cells_where_it_is_enumerated()
    {
        var run = Run("foreach (var item in Seqs.Lib.Flatten(_state.Items, item => _state.Others)) { }");

        Assert.Contains(OnCollection(run, "Others"), access => access.Resource.Selector is not null && access.Operation == AccessOperation.Read &&
                                                             KindOf(run, access) == ExecutionKind.Root);
    }

    [Fact]
    public void Escaped_sequence_reads_its_sources_in_its_unknown_enumeration()
    {
        var run = Run("_state.Kept = Seqs.Lib.Filter(_state.Items, item => true);");

        Assert.Contains(OnCollection(run, "Items"), access => access.Resource.Selector is not null &&
                                                            KindOf(run, access) == ExecutionKind.UnknownEnumeration);
    }

    [Fact]
    public void Escaped_sequence_without_delegates_makes_its_effects_in_its_unknown_enumeration()
    {
        var run = Run("_state.Kept = Seqs.Lib.Once(_state.Items);");

        Assert.Contains(OnCollection(run, "Items"), access => KindOf(run, access) == ExecutionKind.UnknownEnumeration);
        Assert.Contains(run.Run.Of("Hits"), access => access.Resource.Region == PRIMARY && KindOf(run, access) == ExecutionKind.UnknownEnumeration);
    }

    [Fact]
    public void Escaped_sequence_with_an_unresolved_delegate_is_a_dispatch_in_its_unknown_enumeration()
    {
        var run = Run("_state.Kept = Seqs.Lib.Filter(_state.Items, Seqs.Lib.Unknown<Item>());");

        Assert.Contains(run.Run.Of("Hits"), access => access.Operation == AccessOperation.UnknownEffect && access.Resource.Region == PRIMARY &&
                                                      KindOf(run, access) == ExecutionKind.UnknownEnumeration);
    }

    // ---- review F-0063 to F-0066 ----

    [Fact]
    public void Elements_of_a_user_iterator_are_what_it_yields()
    {
        var run = Run("Seqs.Lib.Each(_state.Produce(), item => item.Hits = 1);");

        Assert.Equal([PRIMARY], Written(run, "item.Hits = 1"));
    }

    [Fact]
    public void Elements_of_a_sequence_an_eager_input_builds_are_enumerated_at_the_call()
    {
        var list = Run("Seqs.Lib.EachOf(_state.Items, item => { });");
        var iterator = Run("Seqs.Lib.EachOf(_state.Produce(), item => { });");

        Assert.Contains(OnCollection(list, "Items"), access => access.Resource.Selector is not null && access.Operation == AccessOperation.Read &&
                                                             KindOf(list, access) == ExecutionKind.Root);
        AssertIteratorRunsOnlyInTheWorker(iterator);
        AssertNoUnknownEnumeration(iterator);
    }

    [Fact]
    public void Sequence_of_the_elements_of_a_built_sequence_enumerates_its_source_where_it_is_enumerated()
    {
        var created = Run("var nested = Seqs.Lib.Nest(_state.Produce());");
        var list = Run("foreach (var item in Seqs.Lib.Nest(_state.Items)) { }");
        var iterator = Run("foreach (var item in Seqs.Lib.Nest(_state.Produce())) { }");

        Assert.Empty(created.Run.Of("Count"));
        Assert.Contains(OnCollection(list, "Items"), access => access.Resource.Selector is not null && access.Operation == AccessOperation.Read &&
                                                             KindOf(list, access) == ExecutionKind.Root);
        AssertIteratorRunsOnlyInTheWorker(iterator);
        AssertNoUnknownEnumeration(iterator);
    }

    [Fact]
    public void Two_unresolved_targets_of_one_iterator_argument_are_both_dispatched()
    {
        var run = Run("var a = new Checker();\nvar b = new Checker();\n" +
                      "Func<Item, bool> check = System.Environment.ProcessorCount > 1 ? (Func<Item, bool>)a.Check : b.Check;\n" +
                      "foreach (var item in Seqs.Lib.Filter(_state.Items, check)) { }");

        var receivers = Heap(run).UnresolvedDispatchReceivers.Where(pair => IsWorker(run, pair.Key.Instance))
                                 .SelectMany(pair => pair.Value.Select(receiver => receiver.Region))
                                 .Distinct(StringComparer.Ordinal)
                                 .ToArray();
        Assert.Equal(2, receivers.Length);
    }

    // ---- helpers ----

    /// <summary>The iterator <c>Produce</c>'s write of <c>Count</c> exists, every one in a root execution: the worker's, where it is
    /// enumerated.</summary>
    private static void AssertIteratorRunsOnlyInTheWorker(Case run)
    {
        var writes = run.Run.Of("Count");
        Assert.NotEmpty(writes);
        Assert.All(writes, write => Assert.Equal(ExecutionKind.Root, KindOf(run, write)));
    }

    /// <summary>The accesses of the collection a field of the singleton holds, its structure and its cells.</summary>
    private static IReadOnlyList<Access> OnCollection(Case run, string field) =>
        run.Run.Collection.Accesses.Where(access => access.Resource.CollectionId is not null && access.Resource.AccessPath[0] == field).ToArray();

    /// <summary>A run with the text of the case file it analysed.</summary>
    private sealed record Case(EngineRun Run, string Text);

    private static HeapSolution Heap(Case run) => run.Run.Execution.Heap.Heap;

    private static ExecutionKind KindOf(Case run, Access access) => run.Run.Execution.Analysis.Execution(access.ExecutionId).Kind;

    private static bool IsWorker(Case run, string instanceId) =>
        Heap(run).Instances[instanceId].BodyId.Contains("Worker.ExecuteAsync", StringComparison.Ordinal);

    /// <summary>A member's accesses exist, every one in the worker's own execution.</summary>
    private static void AssertRunsOnlyInTheWorker(Case run, string member)
    {
        var accesses = Worker(run, member);
        Assert.NotEmpty(accesses);
        Assert.All(accesses, access => Assert.Equal(ExecutionKind.Root, KindOf(run, access)));
    }

    private static void AssertNoUnknownEnumeration(Case run) =>
        Assert.DoesNotContain(run.Run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownEnumeration);

    private static IReadOnlyList<Access> Worker(Case run, string member) =>
        run.Run.Of(member).Where(access => access.Symbol.StartsWith("Worker.", StringComparison.Ordinal)).ToArray();

    private static IReadOnlyList<Access> AtLine(Case run, string member, string at) =>
        run.Run.Collection.Accesses.Where(access => access.Resource.Member.Name == member && access.Source.StartLine == Line(run.Text, at)).ToArray();

    /// <summary>The objects the worker writes <c>Hits</c> of at the line containing <paramref name="at"/>.</summary>
    private static string[] Written(Case run, string at) =>
        Worker(run, "Hits").Where(access => access.Operation == AccessOperation.Write && access.Source.StartLine == Line(run.Text, at))
                           .Select(access => access.Resource.Region)
                           .Distinct()
                           .Order(StringComparer.Ordinal)
                           .ToArray();

    private static int Line(string file, string text) =>
        Array.FindIndex(file.Split('\n'), line => line.Contains(text, StringComparison.Ordinal)) + 1 is var found and > 0
            ? found
            : throw new InvalidOperationException($"no line holds '{text}'");

    private static Case Run(string work, string other = "", string action = "", string startup = "")
    {
        var text = Usings + Source(work, other, action, startup);
        var solution = FixtureSolution.Create(new FixtureOptions { MetadataReferences = [Library.Value] }, ("Case.cs", text));
        return new Case(AnalyzeScope(solution, "scope:Fixture", Models(solution)), text);
    }

    /// <summary>The library's project models, resolved as a run resolves them; every entry must fit its member.</summary>
    /// <param name="solution">The fixture solution whose compilation resolves the models.</param>
    /// <param name="text">An optional project model file replacing the default models.</param>
    private static LibraryModels Models(Solution solution, string? text = null)
    {
        var root = Directory.CreateTempSubdirectory("ch-sequence-").FullName;
        try
        {
            var folder = Path.Combine(root, ".concurrency-hunter", "models");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "sequences.json"), text ?? ModelFile(), new UTF8Encoding(false));
            var compilation = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
            var files = ProjectModelFiles.Read(root);
            var (models, rejections) = ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(root, files));
            Assert.Empty(rejections);
            return models;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ModelFile()
    {
        string Entry(string name, string decision) =>
            "{\"member\":\"" + DocumentationCommentId.CreateDeclarationId(LibrarySource.Value.GetTypeByMetadataName("Seqs.Lib")!.GetMembers(name).Single())! +
            "\"," + decision + "}";
        string Lazy(string delegateName, string result) =>
            $$$""" "effects":{},"result":"{{{result}}}","fates":{"{{{delegateName}}}":{"fate":"iterator","inputs":[["elements(arg:source)"]]}}""";
        return "{\"schemaVersion\":1,\"assemblies\":[\"Seqs\"],\"models\":[" + string.Join(",",
            Entry("Filter", Lazy("predicate", "sequence(elements(arg:source))")),
            Entry("Map", Lazy("selector", "sequence(returns:selector)")),
            Entry("Flatten", Lazy("selector", "sequence(elements(returns:selector))")),
            Entry("Order", Lazy("key", "sequence(elements(arg:source))")),
            Entry("Then", Lazy("key", "sequence(elements(arg:source))")),
            Entry("Group", Lazy("key", "sequence(grouping(returns:key,elements(arg:source)))")),
            Entry("Twice", """ "effects":{"source":["reads-deep"]},"result":"sequence(elements(arg:source),elements(arg:source))" """),
            Entry("Once", """ "effects":{"source":["reads-deep"]},"result":"sequence(elements(arg:source))" """),
            Entry("Lazy", """ "effects":{"touched":["writes-arg"]},"result":"sequence(elements(arg:source))" """),
            Entry("Read", """ "effects":{"value":["reads-deep"]}"""),
            Entry("Mark", """ "effects":{"value":["writes-arg"]}"""),
            Entry("Each", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":[["elements(arg:source)"]]}}"""),
            Entry("Collect", """ "effects":{},"result":"collection(elements(returns:maker))","fates":{"maker":{"fate":"invoke-now","inputs":[]}}"""),
            Entry("Wrap", """ "effects":{},"result":"[sequence(elements(arg:source))]" """),
            Entry("EachOf", """ "effects":{},"fates":{"action":{"fate":"invoke-now","inputs":[["elements(sequence(elements(arg:source)))"]]}}"""),
            Entry("Nest", """ "effects":{},"result":"sequence(elements(sequence(elements(arg:source))))" """)) +
            "]}";
    }

    /// <summary>A singleton <c>State</c> a worker does <paramref name="work"/> on, a reader <paramref name="other"/> and a controller's
    /// action <paramref name="action"/>; <paramref name="startup"/> runs in <c>Configure</c>, which its factory registration makes a
    /// startup member.</summary>
    /// <param name="work">The worker's statements.</param>
    /// <param name="other">The reader's statements.</param>
    /// <param name="action">The controller action's statements.</param>
    /// <param name="startup">The startup member's statements.</param>
    private static string Source(string work, string other, string action, string startup) => $$"""
        using System.Collections.Generic;
        using System.Linq;

        public class Item { public int Hits; }
        public sealed class Primary : Item { }
        public sealed class Secondary : Item { }
        public sealed class Keyed : Item { }
        public sealed class Marker { }

        // A method no body the run has implements, called on objects the heap knows: each object its own unresolved target.
        public sealed class Checker
        {
            public extern bool Check(object value);
        }
        public static class Totals
        {
            public static int Sum;
            public static IEnumerable<int>? Escaped;
        }

        public sealed class State
        {
            public readonly Primary First = new Primary();
            public readonly Secondary Second = new Secondary();
            public readonly Keyed Key = new Keyed();
            public readonly List<Item> Items = new();
            public readonly List<Item> Others = new();
            public readonly object Gate = new();
            public IEnumerable<Item>? Kept;
            public Func<int>? Stored;
            public int Count;
            public int Tally;

            public State()
            {
                Items.Add(First);
                Others.Add(Second);
            }

            public IEnumerable<Item> Produce()
            {
                Count = 1;
                yield return First;
            }
        }

        [ApiController]
        public sealed class StateController : ControllerBase
        {
            private readonly State _state;
            public StateController(State state) => _state = state;

            [HttpPost("/state")]
            public void Post()
            {
                {{action}}
            }
        }

        public sealed class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;

            private void Later(IEnumerable<Item> sequence) { foreach (var entry in sequence) { } }

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }
        }

        public sealed class Reader(State state) : BackgroundService
        {
            private readonly State _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{other}}
                return Task.CompletedTask;
            }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddSingleton<Marker>(_ => new Marker()); " +
                      "services.AddHostedService<Worker>(); services.AddHostedService<Reader>(); " + startup);

    private static readonly Lazy<CSharpCompilation> LibrarySource = new(() => CSharpCompilation.Create("Seqs", [CSharpSyntaxTree.ParseText("""
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace Seqs
        {
            public static class Lib
            {
                public static IEnumerable<T> Filter<T>(IEnumerable<T> source, Func<T, bool> predicate) => null!;
                public static IEnumerable<R> Map<T, R>(IEnumerable<T> source, Func<T, R> selector) => null!;
                public static IEnumerable<R> Flatten<T, R>(IEnumerable<T> source, Func<T, IEnumerable<R>> selector) => null!;
                public static IOrderedEnumerable<T> Order<T, K>(IEnumerable<T> source, Func<T, K> key) => null!;
                public static IOrderedEnumerable<T> Then<T, K>(IOrderedEnumerable<T> source, Func<T, K> key) => null!;
                public static IEnumerable<IGrouping<K, T>> Group<T, K>(IEnumerable<T> source, Func<T, K> key) => null!;
                public static IEnumerable<T> Twice<T>(IEnumerable<T> source) => null!;
                public static IEnumerable<T> Once<T>(IEnumerable<T> source) => null!;
                public static IEnumerable<T> Lazy<T>(IEnumerable<T> source, object touched) => null!;
                public static void Read(object value) { }
                public static void Mark(object value) { }
                public static void Each<T>(IEnumerable<T> source, Action<T> action) { }
                public static Func<T, bool> Unknown<T>() => null!;
                public static void Keep(object value) { }
                public static List<T> Collect<T>(Func<IEnumerable<T>> maker) => null!;
                public static IEnumerable<T> Wrap<T>(IEnumerable<T> source) => null!;
                public static void EachOf<T>(IEnumerable<T> source, Action<T> action) { }
                public static IEnumerable<T> Nest<T>(IEnumerable<T> source) => null!;
                public static IEnumerable<T> Immediate<T>(IEnumerable<T> source, T other, Action<T> action) => null!;
                public static IEnumerable<T> ImmediateMake<T>(Func<IEnumerable<T>> make, T other, Action<T> action) => null!;
            }
        }
        """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

    private static readonly Lazy<MetadataReference> Library = new(() =>
    {
        using var stream = new MemoryStream();
        var emitted = LibrarySource.Value.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    });
}
