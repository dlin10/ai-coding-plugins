using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A write through a <c>ref</c> local or a <c>ref</c> parameter writes the storage it refers to and is no new version of
/// the variable; only binding it is (<see cref="SsaPlan.DefinesVersion"/>, question 169). Each write form is checked for a
/// <c>ref</c> local and for a <c>ref</c> parameter: in the IR the variable keeps the one value its binding gave it, and the engine
/// pairs the write with another worker's write of the storage the reference names.</summary>
public sealed class RefVariableVersionTests
{
    /// <summary>Every form of a write through the reference.</summary>
    /// <param name="parameter">Whether the reference is a <c>ref</c> parameter rather than a <c>ref</c> local.</param>
    /// <param name="type">The type of the storage the reference names.</param>
    /// <param name="field">The field of <c>State</c> the reference is bound to.</param>
    /// <param name="write">The write through <c>r</c>.</param>
    /// <param name="other">The other worker's write of the same storage.</param>
    /// <param name="resource">The end of the identity of the resource both writes touch: the field, and the struct path below it.</param>
    [Theory]
    [InlineData(false, "int", "Cell", "r = 1;", "_state.Cell = 2;", "State.Cell:Field")]
    [InlineData(false, "Pair", "Point", "r.X = 1;", "_state.Point.X = 2;", "State.Point:Field/Pair.X:Field")]
    [InlineData(false, "int", "Cell", "r += 1;", "_state.Cell = 2;", "State.Cell:Field")]
    [InlineData(false, "int", "Cell", "r++;", "_state.Cell = 2;", "State.Cell:Field")]
    [InlineData(false, "string", "Text", "r ??= \"a\";", "_state.Text = \"b\";", "State.Text:Field")]
    [InlineData(false, "int", "Cell", "(r, _) = (1, 2);", "_state.Cell = 2;", "State.Cell:Field")]
    [InlineData(false, "int", "Cell", "State.Bump(ref r);", "_state.Cell = 2;", "State.Cell:Field")]
    [InlineData(false, "int", "Cell", "State.Set(out r);", "_state.Cell = 2;", "State.Cell:Field")]
    [InlineData(true, "int", "Cell", "r = 1;", "_state.Cell = 2;", "State.Cell:Field")]
    [InlineData(true, "Pair", "Point", "r.X = 1;", "_state.Point.X = 2;", "State.Point:Field/Pair.X:Field")]
    [InlineData(true, "int", "Cell", "r += 1;", "_state.Cell = 2;", "State.Cell:Field")]
    [InlineData(true, "int", "Cell", "r++;", "_state.Cell = 2;", "State.Cell:Field")]
    [InlineData(true, "string", "Text", "r ??= \"a\";", "_state.Text = \"b\";", "State.Text:Field")]
    [InlineData(true, "int", "Cell", "(r, _) = (1, 2);", "_state.Cell = 2;", "State.Cell:Field")]
    [InlineData(true, "int", "Cell", "State.Bump(ref r);", "_state.Cell = 2;", "State.Cell:Field")]
    [InlineData(true, "int", "Cell", "State.Set(out r);", "_state.Cell = 2;", "State.Cell:Field")]
    public async Task Write_through_a_reference_is_no_version_and_writes_the_storage_it_names(bool parameter, string type, string field,
                                                                                           string write, string other, string resource)
    {
        var body = await Lower(parameter ? $"void M(ref {type} r) {{ {write} }}" : $"void M() {{ ref {type} r = ref _state.{field}; {write} }}");
        Assert.Single(body.Values, value => value.Name == "r");
        Assert.DoesNotContain(Operations<IrUnknownOperation>(body), unknown => unknown.Reason == "unproven-reference");

        var run = AnalyzeCase(parameter ? $"_state.Write(ref _state.{field});" : $"ref {type} r = ref _state.{field}; {write}", other,
                              parameter ? $"public void Write(ref {type} r) {{ {write} }}" : "");
        Assert.Contains(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Identity.EndsWith("|" + resource, StringComparison.Ordinal));
    }

    /// <summary>Binding the reference again is a new version, and a write after it goes to the storage the new binding names.</summary>
    /// <param name="parameter">Whether the reference is a <c>ref</c> parameter rather than a <c>ref</c> local.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rebinding_a_reference_defines_a_version(bool parameter)
    {
        var body = await Lower(parameter ? "void M(ref int r) { r = ref _state.Other; r = 1; }"
                                         : "void M() { ref int r = ref _state.Cell; r = ref _state.Other; r = 1; }");
        Assert.Equal(2, body.Values.Count(value => value.Name == "r"));

        var run = AnalyzeCase(parameter ? "_state.Write(ref _state.Cell);" : "ref int r = ref _state.Cell; r = ref _state.Other; r = 1;",
                              "_state.Other = 2;",
                              parameter ? "public void Write(ref int r) { r = ref Other; r = 1; }" : "");
        Assert.Contains(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Other");
    }

    /// <summary>A loop that writes through an unchanged reference carries the reference itself round its back edge, which names the
    /// storage it was bound to: no reference is left unproven.</summary>
    /// <param name="parameter">Whether the reference is a <c>ref</c> parameter rather than a <c>ref</c> local.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Loop_writing_through_an_unchanged_reference_adds_no_unproven_reference(bool parameter)
    {
        var loop = "for (var i = 0; i < 4; i++) r += i;";
        var once = "r += 1;";
        EngineRun Run(string write) =>
            AnalyzeCase(parameter ? "_state.Write(ref _state.Cell);" : $"ref int r = ref _state.Cell; {write}", "_state.Cell = 2;",
                        parameter ? $"public void Write(ref int r) {{ {write} }}" : "");

        var looped = Run(loop);
        Assert.Equal(Run(once).Counter(CoverageCounters.UNPROVEN_REFERENCE), looped.Counter(CoverageCounters.UNPROVEN_REFERENCE));
        Assert.Contains(looped.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Cell");
    }

    /// <summary>How a cell of <see cref="A_read_through_a_ref_parameter_sees_the_write_through_it"/> writes the shared item through
    /// <c>p</c>.</summary>
    public enum ParameterWrite
    {
        /// <summary><c>p = Shared;</c></summary>
        Assign,

        /// <summary><c>Give(out p, Shared);</c></summary>
        OutArgument,

        /// <summary><c>Swap(ref p, Shared);</c></summary>
        RefArgument
    }

    /// <summary>How a reference can name the parameter, alone or among other locations.</summary>
    public enum ParameterBinding
    {
        Direct,
        Local,
        Conditional,
        Rebound
    }

    /// <summary>What lies between the write through <c>p</c> and the read through it.</summary>
    public enum Separation
    {
        /// <summary>Nothing: the read follows in the same block.</summary>
        SameBlock,

        /// <summary>The write on both arms of a branch, the read after their join.</summary>
        BranchJoin,

        /// <summary>A call of a body with no reference argument.</summary>
        CallBetween,

        /// <summary>The read in a loop's body, the write before the loop.</summary>
        Loop
    }

    /// <summary>How the read through <c>p</c> uses the item.</summary>
    public enum ParameterRead
    {
        /// <summary><c>Seen = p.Count;</c></summary>
        FieldRead,

        /// <summary><c>p.Count++;</c></summary>
        ReadModifyWrite
    }

    public static TheoryData<ParameterWrite, Separation, ParameterRead, ParameterBinding> ParameterCells()
    {
        var rows = new TheoryData<ParameterWrite, Separation, ParameterRead, ParameterBinding>();
        foreach (var write in Enum.GetValues<ParameterWrite>())
        {
            foreach (var separation in Enum.GetValues<Separation>())
            {
                foreach (var read in Enum.GetValues<ParameterRead>())
                    foreach (var binding in Enum.GetValues<ParameterBinding>())
                        rows.Add(write, separation, read, binding);
            }
        }
        return rows;
    }

    /// <summary>A read through a <c>ref</c> or <c>out</c> parameter after a write through it sees the value written, whatever separates
    /// them (R6): the caller hands over a local the heap holds no storage of, so only the write itself tells the read what <c>p</c> holds.
    /// The read is an access on the shared item, which pairs with the other worker's write of it.</summary>
    /// <param name="write">How the shared item is written through <c>p</c>.</param>
    /// <param name="separation">What lies between the write and the read.</param>
    /// <param name="read">How the read uses the item.</param>
    /// <param name="binding">How the written reference names the parameter.</param>
    [Theory]
    [MemberData(nameof(ParameterCells))]
    public void A_read_through_a_ref_parameter_sees_the_write_through_it(ParameterWrite write, Separation separation, ParameterRead read,
                                                                       ParameterBinding binding)
    {
        var reference = binding == ParameterBinding.Direct ? "p" : "r";
        var bind = binding switch
        {
            ParameterBinding.Direct => "",
            ParameterBinding.Local => "ref Item r = ref p;",
            ParameterBinding.Conditional => "ref Item r = ref (flag ? ref p : ref Shared);",
            _ => "ref Item r = ref p; if (flag) r = ref Shared;"
        };
        var written = write switch
        {
            ParameterWrite.Assign => $"{reference} = Shared;",
            ParameterWrite.OutArgument => $"Give(out {reference}, Shared);",
            _ => $"Swap(ref {reference}, Shared);"
        };
        var used = read == ParameterRead.FieldRead ? "Seen = p.Count;" : "p.Count++;";
        var body = separation switch
        {
            Separation.SameBlock => $"{written} {used}",
            Separation.BranchJoin => $"if (flag) {{ {written} }} else {{ {written} }} {used}",
            Separation.CallBetween => $"{written} Noop(); {used}",
            _ => $"{written} for (var i = 0; i < 2; i++) {{ {used} }}"
        };

        var run = ParameterCase($"{bind} {body}");
        var cell = $"{write}/{separation}/{read}/{binding}";
        Assert.True(run.Accesses("Count").Any(access => access.Symbol.Contains("Test", StringComparison.Ordinal) &&
                                                        access.Operation == (read == ParameterRead.FieldRead ? AccessOperation.Read
                                                                                                            : AccessOperation.ReadModifyWrite)),
                    $"{cell}: the read's access on the shared item");
        Assert.True(run.Pairs.Pairs.Any(pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Count"), $"{cell}: the pair with the other worker");

        var returned = ParameterCase($"{bind} {written}", readAfterCall: true);
        Assert.Contains(returned.Accesses("Count"), access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal));
        Assert.Contains(returned.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Count");
    }

    /// <summary>A possible write joins the incoming parameter value: when the write targets Alternate instead, p still holds the shared
    /// item passed in, so both its access and its outgoing transfer retain the pair.</summary>
    /// <param name="binding">A binding that can name p and another location.</param>
    [Theory]
    [InlineData(ParameterBinding.Conditional)]
    [InlineData(ParameterBinding.Rebound)]
    public void A_possible_parameter_write_preserves_its_incoming_value(ParameterBinding binding)
    {
        var bind = binding == ParameterBinding.Conditional ? "ref Item r = ref (flag ? ref p : ref Alternate);"
                                                          : "ref Item r = ref p; if (flag) r = ref Alternate;";
        var run = ParameterCase($"{bind} r = new Item(); Noop(); p.Count++;", readAfterCall: true, sharedIncoming: true);
        Assert.Contains(run.Accesses("Count"), access => access.Symbol.Contains("Test", StringComparison.Ordinal));
        Assert.Contains(run.Accesses("Count"), access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal));
        Assert.Contains(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Count" &&
                                               (pair.First.Symbol.Contains("FirstWorker", StringComparison.Ordinal) ||
                                                pair.Second.Symbol.Contains("FirstWorker", StringComparison.Ordinal)));
    }

    /// <summary>Where the rule stops: a write through <c>p</c> of an object the body creates leaves the read on that object, which no
    /// other worker touches, across a branch join as in the same block.</summary>
    /// <param name="separation">What lies between the write and the read.</param>
    [Theory]
    [InlineData(Separation.SameBlock)]
    [InlineData(Separation.BranchJoin)]
    [InlineData(Separation.CallBetween)]
    public void A_read_after_writing_a_fresh_object_through_a_ref_parameter_pairs_with_nothing(Separation separation)
    {
        var body = separation switch
        {
            Separation.SameBlock => "p = new Item(); p.Count++;",
            Separation.BranchJoin => "if (flag) { p = new Item(); } else { p = new Item(); } p.Count++;",
            _ => "p = new Item(); Noop(); p.Count++;"
        };

        var run = ParameterCase(body);
        Assert.Contains(run.Accesses("Count"), access => access.Symbol.Contains("Test", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Count");
    }

    /// <summary>A case whose first worker hands a local of its own to <c>State.Test(ref Item p, bool flag)</c> and whose second worker
    /// writes the shared item's <c>Count</c>.</summary>
    /// <param name="body">The body of <c>Test</c>.</param>
    /// <param name="readAfterCall">Whether the caller reads the parameter's outgoing value.</param>
    /// <param name="sharedIncoming">Whether the caller initially hands the shared item to p, rather than null.</param>
    private static EngineRun ParameterCase(string body, bool readAfterCall = false, bool sharedIncoming = false) =>
        AnalyzeCase($"State.Item local = {(sharedIncoming ? "_state.Shared" : "null")}; _state.Test(ref local, stoppingToken.IsCancellationRequested); " +
                    (readAfterCall ? "local.Count++;" : ""), "_state.Shared.Count = 2;", $$"""
            public sealed class Item { public int Count; }
            public Item Shared = new();
            public Item Alternate = new();
            public int Seen;
            public static void Noop() { }
            public static void Give(out Item p, Item value) => p = value;
            public static void Swap(ref Item p, Item value) => p = value;
            public void Test(ref Item p, bool flag) { {{body}} }
        """);

    /// <summary>A ref parameter read after a whole struct write still reads shared storage, including contents another worker stores.</summary>
    /// <param name="readContents">Whether the copy's reference field is used after the storage read.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_saved_ref_parameter_value_preserves_the_storage_read(bool readContents)
    {
        var use = readContents ? "x.Ref.Count++;" : "";
        var members = $$"""
            public sealed class Item { public int Count; }
            public struct S { public Item Ref; }
            public S Slot;
            public Item Shared = new();
            public void ReadThrough(ref S p) { p = new S(); var x = p; {{use}} }
        """;
        var other = "_state.Slot = new State.S { Ref = _state.Shared }; _state.Shared.Count++;";
        var direct = AnalyzeCase($"_state.Slot = new State.S(); var x = _state.Slot; {use}", other, members);
        var through = AnalyzeCase("_state.ReadThrough(ref _state.Slot);", other, members);
        foreach (var run in new[] { direct, through })
        {
            Assert.Contains(run.Accesses("Slot"), access => access.Operation == AccessOperation.Read);
            Assert.Contains(run.Accesses("Slot"), access => access.Operation == AccessOperation.Write);
            if (readContents)
            {
                Assert.Equal(2, run.Accesses("Count").Count(access => access.Operation == AccessOperation.ReadModifyWrite));
                Assert.Contains(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Count");
            }
        }
    }

    public static TheoryData<ParameterWrite, Separation, bool> LocalCells()
    {
        var rows = new TheoryData<ParameterWrite, Separation, bool>();
        foreach (var write in Enum.GetValues<ParameterWrite>())
            foreach (var separation in Enum.GetValues<Separation>())
                foreach (var readAlias in new[] { false, true })
                    rows.Add(write, separation, readAlias);
        return rows;
    }

    /// <summary>A write through a local-backed alias reaches a read of either the alias or its ordinary local referent.</summary>
    /// <param name="write">The write through the alias.</param>
    /// <param name="separation">What separates the write and read.</param>
    /// <param name="readAlias">Whether the read uses the alias rather than its referent.</param>
    [Theory]
    [MemberData(nameof(LocalCells))]
    public void A_local_backed_reference_preserves_the_value_written(ParameterWrite write, Separation separation, bool readAlias)
    {
        var written = write switch
        {
            ParameterWrite.Assign => "r = _state.Shared;",
            ParameterWrite.OutArgument => "State.Give(out r, _state.Shared);",
            _ => "State.Swap(ref r, _state.Shared);"
        };
        var used = $"{(readAlias ? "r" : "v")}.Count++;";
        var body = separation switch
        {
            Separation.SameBlock => $"{written} {used}",
            Separation.BranchJoin => $"if (stoppingToken.IsCancellationRequested) {{ {written} }} else {{ {written} }} {used}",
            Separation.CallBetween => $"{written} State.Noop(); {used}",
            _ => $"{written} for (var i = 0; i < 2; i++) {{ {used} }}"
        };
        var run = AnalyzeCase($"State.Item v = null; ref State.Item r = ref v; {body}", "_state.Shared.Count++;", LocalMembers);
        Assert.Contains(run.Accesses("Count"), access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal));
        Assert.Contains(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Count");
    }

    /// <summary>A local alias does not publish a fresh object, and a copied value does not become an alias.</summary>
    /// <param name="alias">Whether the variable is a reference alias or a by-value copy.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_local_write_does_not_change_an_independent_value(bool alias)
    {
        var bind = alias ? "ref State.Item r = ref v; r = new State.Item();" : "State.Item r = v; r = _state.Shared;";
        var run = AnalyzeCase($"State.Item v = new State.Item(); {bind} v.Count++;", "_state.Shared.Count++;", LocalMembers);
        Assert.Contains(run.Accesses("Count"), access => access.Symbol.Contains("FirstWorker", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Pairs.Pairs, pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Count");
    }

    /// <summary>Local reference reads retain incoming values, direct writes to the referent and each arm of a conditional binding.</summary>
    /// <param name="scenario">The row's identity, kept distinct when the runner truncates the source argument.</param>
    /// <param name="body">The first worker's aliasing and use.</param>
    [Theory]
    [InlineData("incoming-local", "State.Item v = _state.Shared; ref State.Item r = ref v; r.Count++;")]
    [InlineData("direct-write", "State.Item v = null; ref State.Item r = ref v; v = _state.Shared; r.Count++;")]
    [InlineData("conditional-referent", "State.Item v = null; State.Item w = null; ref State.Item r = ref (stoppingToken.IsCancellationRequested ? ref v : ref w); r = _state.Shared; v.Count++;")]
    [InlineData("conditional-alias", "State.Item v = null; State.Item w = null; ref State.Item r = ref (stoppingToken.IsCancellationRequested ? ref v : ref w); r = _state.Shared; r.Count++;")]
    [InlineData("incoming-parameter", "_state.Read(_state.Shared);")]
    public void A_local_reference_reads_the_referents_value(string scenario, string body)
    {
        var run = AnalyzeCase(body, "_state.Shared.Count++;", LocalMembers +
                              "public void Read(Item value) { ref Item r = ref value; r.Count++; }");
        Assert.True(run.Pairs.Pairs.Any(pair => AcrossWorkers(pair) && pair.Resource.Member.Name == "Count"), $"{scenario}: {body}");
    }

    private const string LocalMembers = """
        public sealed class Item { public int Count; }
        public Item Shared = new();
        public static void Noop() { }
        public static void Give(out Item p, Item value) => p = value;
        public static void Swap(ref Item p, Item value) => p = value;
        """;

    [Fact]
    public async Task Ref_parameter_written_in_a_loop_has_no_phi()
    {
        var body = await Lower("void M(ref int r) { for (var i = 0; i < 4; i++) r += i; }");

        var parameter = Assert.Single(body.Values, value => value.Name == "r");
        Assert.DoesNotContain(Operations<IrPhiOperation>(body), phi => body.Values.Single(value => value.Id == phi.TargetValue).Name == "r" ||
                                                                      phi.Inputs.Any(input => input.Value == parameter.Id));
    }

    private const string StateMembers = """
            public int Cell;
            public int Other;
            public string Text;
            public Pair Point;
            public static void Bump(ref int value) => value++;
            public static void Set(out int value) => value = 1;
        """;

    private static async Task<IrBody> Lower(string method)
    {
        var solution = FixtureSolution.Create(("Case.cs", $$"""
            public struct Pair { public int X; }
            public sealed class State
            {
            {{StateMembers}}
            }
            class C
            {
                State _state = new();
                {{method}}
            }
            """));
        var compilation = await solution.Projects.Single().GetCompilationAsync() ?? throw new InvalidOperationException();
        var type = compilation.GetTypeByMetadataName("C") ?? throw new InvalidOperationException("Type C was not found.");
        var symbol = type.GetMembers("M").OfType<IMethodSymbol>().Single();
        return IrLowering.Lower(symbol, compilation, ROOT_DIRECTORY, CancellationToken.None).Body;
    }

    private static T[] Operations<T>(IrBody body) where T : IrOperation =>
        body.Blocks.SelectMany(block => block.Operations).OfType<T>().ToArray();

    private static bool AcrossWorkers(AccessPair pair) =>
        pair.First.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
        pair.Second.Root.Symbol.Contains("SecondWorker", StringComparison.Ordinal) ||
        pair.Second.Root.Symbol.Contains("FirstWorker", StringComparison.Ordinal) &&
        pair.First.Root.Symbol.Contains("SecondWorker", StringComparison.Ordinal);

    private static EngineRun AnalyzeCase(string first, string second, string stateMembers) => Analyze(Usings + $$"""
        public struct Pair { public int X; }

        public sealed class State
        {
        {{StateMembers}}
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
