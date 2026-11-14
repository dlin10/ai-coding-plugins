using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class AwaitedRunsBoundaryMatrixTests
{
    public static readonly string[] Forms =
    [
        "lambda", "method", "local", "phi-async", "phi-sync", "phi-call", "sync", "add-distinct", "append-distinct", "combine-distinct",
        "add-same", "append-same", "combine-same", "parameter", "field", "two-awaited", "two-mixed", "sync-completed", "sync-field",
        "sync-two", "local-function"
    ];
    /// <summary>The ordered forms whose next-run pair remains wider because one continuation of an admitted call is not ordered
    /// against another continuation of the same call (#416).</summary>
    private static readonly (string Form, string Reason)[] WiderForms =
    [
        ("phi-sync", "#416: one continuation of an admitted call is not ordered against another continuation of the same call.")
    ];

    public static readonly string[] Places =
    [
        "direct", "local", "field", "parameter", "return", "conditional-field", "worker", "http",
        "parameter-completed", "parameter-delay", "field-completed", "field-delay",
        "overwrite-completed", "overwrite-delay", "conditional-overwrite-completed", "conditional-overwrite-delay",
        "local-completed", "local-delay", "ref-overwrite", "foreign-overwrite", "ref-field", "null-before-field"
    ];
    /// <summary>The pair a delegate cell reads: the continuation with the code after the call, or with the synchronous part of the next
    /// run.</summary>
    public static readonly string[] Pairs = ["after", "next"];
    public static readonly string[] Handles = ["call-task", "conditional", "wait-all-one", "wait-all-two", "when-all", "wait", "get", "result"];
    public static IEnumerable<object[]> DelegateCells() => from form in Forms from pair in Pairs select new object[] { form, pair };
    public static IEnumerable<object[]> WaitCells() => from place in Places from handle in Handles select new object[] { place, handle };

    /// <summary>Every delegate and wait axis is covered; each wider form is ordered and names the missing order between
    /// continuations of one admitted call (#416).</summary>
    [Fact]
    public void There_are_17_delegate_forms_and_4_more_on_2_pairs_and_176_wait_cells_covering_every_axis()
    {
        var delegates = DelegateCells().ToArray();
        Assert.Equal(42, delegates.Length);
        Assert.Equal(Forms.Length, Forms.Distinct().Count());
        Assert.All(Pairs, pair => Assert.Equal(21, delegates.Count(cell => (string)cell[1] == pair)));
        Assert.All(WiderForms, wider =>
        {
            Assert.Contains(wider.Form, Forms);
            Assert.True(Ordered(wider.Form), $"{wider.Form} expects the next-run pair, so it cannot be wider.");
            Assert.False(string.IsNullOrWhiteSpace(wider.Reason));
        });
        var cells = WaitCells().ToArray();
        Assert.Equal(176, cells.Length);
        Assert.All(Places, place => Assert.Equal(8, cells.Count(cell => (string)cell[0] == place)));
        Assert.All(Handles, handle => Assert.Equal(22, cells.Count(cell => (string)cell[1] == handle)));
    }

    /// <summary>Each delegate form keeps exactly its required pairs, except the next-run pairs in <see cref="WiderForms"/> whose
    /// continuations of one admitted call are not ordered against each other (#416).</summary>
    /// <param name="form">The delegate form.</param>
    /// <param name="pair">Whether to read the pair after the call or with the next run's synchronous part.</param>
    [Theory]
    [MemberData(nameof(DelegateCells))]
    public void Every_delegate_form_obeys_the_creation_and_async_boundary(string form, string pair)
    {
        // The second pair needs a synchronous part in every run, and the call in a body one async call enters once and awaits at once.
        var next = pair == "next";
        var A = next ? "async () => { _state.C = 3; await Task.Yield(); _state.C = 1; }" : "async () => { await Task.Yield(); _state.C = 1; }";
        var B = A;
        var method = next ? "NextAsync" : "WorkAsync";
        var locals = $"Func<Task> a = {A}; Func<Task> b = {B};";
        // A local function of the calling body, passed as a method group, is a delegate that body creates.
        if (form == "local-function")
            locals += "\nasync Task Local() " + A[A.IndexOf('{')..];
        var expression = form switch
        {
            "lambda" => A,
            "method" => method,
            "local" => "a",
            "local-function" => "Local",
            "phi-async" => "_state.Choose ? a : b",
            "phi-sync" => $"_state.Choose ? a : () => {method}()",
            "phi-call" => "_state.Choose ? a : Runs.Lib.Unknown()",
            "sync" => $"() => {method}()",
            "add-distinct" => "a + b",
            "append-distinct" => "f",
            "combine-distinct" => "(Func<Task>)Delegate.Combine(a, b)",
            "add-same" => "a + a",
            "append-same" => "f",
            "combine-same" => "(Func<Task>)Delegate.Combine(a, a)",
            "parameter" => "f",
            "field" => "_state.F",
            "two-awaited" or "two-mixed" => A,
            // A non-async target runs as the async call it returns only when every return is that one call's task.
            "sync-completed" => $"() => {{ if (_state.Choose) return {method}(); return Task.CompletedTask; }}",
            "sync-field" => $"() => {{ _state.Task = {method}(); return _state.Task; }}",
            "sync-two" => $"() => {{ if (_state.Choose) return {method}(); return {method}(); }}",
            _ => throw new ArgumentOutOfRangeException(nameof(form))
        };
        if (form.StartsWith("append", StringComparison.Ordinal))
            locals += "Func<Task> f = a; f += " + (form == "append-same" ? "a;" : "b;");
        var call = form is "two-awaited" or "two-mixed"
            ? $"Runs.Lib.{(form == "two-awaited" ? "Two" : "Mixed")}({expression}, {B})"
            : $"Runs.Lib.Run({expression})";
        var work = locals + "\nawait " + call + ";\n_state.C = 2;";
        var members = "";
        if (form == "parameter")
        {
            work = $"await Helper({A});";
            members = "private async Task Helper(Func<Task> f) { await Runs.Lib.Run(f);\n_state.C = 2; }";
        }
        if (form == "field")
        {
            work = "await Runs.Lib.Run(_state.F);\n_state.C = 2;";
        }
        if (next && form != "parameter")
        {
            // The calling body is the helper: a field it reads is set by its caller, a delegate it creates stays its own.
            members = "private async Task Helper() { " + (form == "field" ? "" : locals) + "\nawait " + (form == "field" ? "Runs.Lib.Run(_state.F)" : call) +
                      ";\n_state.C = 2; }";
            work = (form == "field" ? $"_state.F = {A};\n" : "") + "await Helper();";
        }
        if (next)
            members += "\nprivate async Task NextAsync() { _state.C = 3; await Task.Yield(); _state.C = 1; }";
        // Another writer keeps the field from naming only the target's own task.
        var run = AwaitedRunsFixture.Run(work, members: members, other: form == "sync-field" ? "_state.Task = Task.Delay(1);" : "",
                                         fieldDelegate: form == "field" && !next);
        var ordered = Ordered(form);
        AwaitedRunsFixture.AssertPairs(run, after: !ordered, self: !ordered);
        // Outside the admission the later runs' synchronous part is in the continuation too, and nothing orders an unadmitted call's runs.
        if (next)
        {
            if (WiderForms.Any(wider => wider.Form == form))
            {
                var error = Record.Exception(() => AwaitedRunsFixture.AssertNextRun(run, pair: true));
                Assert.True(error is null, $"{form}: listed as wider, but it is exact now; take it off the list. {error}");
            }
            else
                AwaitedRunsFixture.AssertNextRun(run, pair: !ordered);
        }
    }

    /// <summary>Whether the delegate form is admitted and its runs are required to be ordered.</summary>
    /// <param name="form">The delegate form.</param>
    private static bool Ordered(string form) => Array.IndexOf(Forms, form) < 4 || form is "sync" or "phi-sync" or "local-function";

    [Theory]
    [MemberData(nameof(WaitCells))]
    public void Every_wait_proves_all_runs_only_with_one_excused_call_task(string place, string handle)
    {
        const string CALL = "Runs.Lib.TaskBare(async () => { await Task.Yield(); _state.C = 1; return 1; })";
        string Wait(string task) => handle switch
        {
            "call-task" => "await " + task + ";",
            "conditional" => "await (_state.Choose ? " + task + " : other);",
            "wait-all-one" => "Task.WaitAll(" + task + ");",
            "wait-all-two" => "Task.WaitAll(" + task + ", other);",
            "when-all" => "await Task.WhenAll(" + task + ", other);",
            "wait" => task + ".Wait();",
            "get" => task + ".GetAwaiter().GetResult();",
            "result" => "_ = ((Task<int>)" + task + ").Result;",
            _ => throw new ArgumentOutOfRangeException(nameof(handle))
        };
        const string OTHER = "var other = Task.Run(() => { });\n";
        var work = "var t = " + CALL + ";\n";
        var members = "";
        var reader = "";
        switch (place)
        {
            case "direct":
                work = OTHER + Wait(CALL) + "\n_state.C = 2;";
                break;
            case "local":
                work += "var copy = t;\n" + OTHER + Wait("copy") + "\n_state.C = 2;";
                break;
            case "field":
                work += "if (_state.Choose) { _state.Task = t; } else { _state.Task = t; }\n" + OTHER + Wait("_state.Task") + "\n_state.C = 2;";
                break;
            case "parameter":
                work += "await Helper(t);";
                members = "private async Task Helper(Task t) { " + OTHER + Wait("t") + "\n_state.C = 2; }";
                break;
            case "return":
                work = "var t = Make();\n" + OTHER + Wait("t") + "\n_state.C = 2;";
                members = "private Task Make() { return " + CALL + "; }";
                break;
            case "conditional-field":
                work += "if (_state.Choose) { _state.Task = t; }\n" + OTHER + Wait("_state.Task") + "\n_state.C = 2;";
                break;
            case "parameter-completed":
            case "parameter-delay":
                work += "await Helper(_state.Choose ? t : " + (place == "parameter-completed" ? "Task.CompletedTask" : "Task.Delay(1)") + ");";
                members = "private async Task Helper(Task t) { " + OTHER + Wait("t") + "\n_state.C = 2; }";
                break;
            case "local-completed":
            case "local-delay":
                work += "Task chosen = _state.Choose ? t : " + (place == "local-completed" ? "Task.CompletedTask" : "Task.Delay(1)") + ";\n" +
                        OTHER + Wait("chosen") + "\n_state.C = 2;";
                break;
            case "field-completed":
            case "field-delay":
                work += "_state.Task = _state.Choose ? t : " + (place == "field-completed" ? "Task.CompletedTask" : "Task.Delay(1)") + ";\n" +
                        OTHER + Wait("_state.Task") + "\n_state.C = 2;";
                break;
            case "overwrite-completed":
            case "overwrite-delay":
            case "conditional-overwrite-completed":
            case "conditional-overwrite-delay":
                var overwrite = "_state.Task = " + (place.EndsWith("completed", StringComparison.Ordinal) ? "Task.CompletedTask" : "Task.Delay(1)") + ";";
                work += "_state.Task = t;\n" + (place.StartsWith("conditional", StringComparison.Ordinal) ? "if (_state.Choose) { " + overwrite + " }" : overwrite) +
                        "\n" + OTHER + Wait("_state.Task") + "\n_state.C = 2;";
                break;
            case "ref-overwrite":
                work += "_state.Task = t; Overwrite(ref _state.Task);\n" + OTHER + Wait("_state.Task") + "\n_state.C = 2;";
                members = "private void Overwrite(ref Task task) { task = Task.Delay(1); }";
                break;
            case "foreign-overwrite":
                work += "_state.Task = t;\n" + OTHER + Wait("_state.Task") + "\n_state.C = 2;";
                reader = "_state.Task = Task.Delay(1);";
                break;
            case "ref-field":
                work += "Keep(ref _state.Task, t);\n" + OTHER + Wait("_state.Task") + "\n_state.C = 2;";
                members = "private void Keep(ref Task task, Task value) { task = value; }";
                break;
            case "null-before-field":
                work += "_state.Task = null; _state.Task = t;\n" + OTHER + Wait("_state.Task") + "\n_state.C = 2;";
                break;
            case "worker":
            case "http":
                work += "_state.Task = t;";
                reader = (place == "http" ? "// http\n" : "") + OTHER + Wait("_state.Task") + "\n_state.C = 2;";
                break;
        }
        var run = AwaitedRunsFixture.Run(work, members: members, other: reader);
        // A store through a ref parameter still leaves the field handle unproven; it must not excuse a later overwrite either.
        var ordered = (Array.IndexOf(Places, place) < 5 || place == "null-before-field") && handle != "conditional";
        AwaitedRunsFixture.AssertPairs(run, after: !ordered, self: false);
        if (!ordered)
        {
            var waitingBody = place is "parameter" or "parameter-completed" or "parameter-delay" ? "Worker.Helper(System.Threading.Tasks.Task)" :
                              place == "worker" ? "Reader.ExecuteAsync(System.Threading.CancellationToken)" :
                              place == "http" ? "ReaderController.Read" : "Worker.ExecuteAsync(System.Threading.CancellationToken)";
            Assert.Contains(run.Execution.Analysis.UnprovenJoins, site => site.BodyId.EndsWith(waitingBody, StringComparison.Ordinal));
        }
    }
}
