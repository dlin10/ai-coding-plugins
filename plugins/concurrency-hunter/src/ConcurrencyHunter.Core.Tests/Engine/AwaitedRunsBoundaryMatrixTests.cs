using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class AwaitedRunsBoundaryMatrixTests
{
    public static readonly string[] Forms =
    [
        "lambda", "method", "local", "phi-async", "phi-sync", "phi-call", "sync", "add-distinct", "append-distinct", "combine-distinct",
        "add-same", "append-same", "combine-same", "parameter", "field", "two-awaited", "two-mixed"
    ];
    public static readonly string[] Places =
    [
        "direct", "local", "field", "parameter", "return", "conditional-field", "worker", "http",
        "parameter-completed", "parameter-delay", "field-completed", "field-delay",
        "overwrite-completed", "overwrite-delay", "conditional-overwrite-completed", "conditional-overwrite-delay",
        "local-completed", "local-delay", "ref-overwrite", "foreign-overwrite", "ref-field", "null-before-field"
    ];
    public static readonly string[] Handles = ["call-task", "conditional", "wait-all-one", "wait-all-two", "when-all", "wait", "get", "result"];
    public static IEnumerable<object[]> DelegateCells() => Forms.Select(form => new object[] { form });
    public static IEnumerable<object[]> WaitCells() => from place in Places from handle in Handles select new object[] { place, handle };

    [Fact]
    public void There_are_17_delegate_and_40_wait_cells_covering_every_axis()
    {
        Assert.Equal(17, DelegateCells().Count());
        Assert.Equal(Forms.Length, Forms.Distinct().Count());
        var cells = WaitCells().ToArray();
        Assert.Equal(176, cells.Length);
        Assert.All(Places, place => Assert.Equal(8, cells.Count(cell => (string)cell[0] == place)));
        Assert.All(Handles, handle => Assert.Equal(22, cells.Count(cell => (string)cell[1] == handle)));
    }

    [Theory]
    [MemberData(nameof(DelegateCells))]
    public void Every_delegate_form_obeys_the_creation_and_async_boundary(string form)
    {
        const string A = "async () => { await Task.Yield(); _state.C = 1; }";
        const string B = "async () => { await Task.Yield(); _state.C = 1; }";
        var locals = $"Func<Task> a = {A}; Func<Task> b = {B};";
        var expression = form switch
        {
            "lambda" => A,
            "method" => "WorkAsync",
            "local" => "a",
            "phi-async" => "_state.Choose ? a : b",
            "phi-sync" => "_state.Choose ? a : () => WorkAsync()",
            "phi-call" => "_state.Choose ? a : Runs.Lib.Unknown()",
            "sync" => "() => WorkAsync()",
            "add-distinct" => "a + b",
            "append-distinct" => "f",
            "combine-distinct" => "(Func<Task>)Delegate.Combine(a, b)",
            "add-same" => "a + a",
            "append-same" => "f",
            "combine-same" => "(Func<Task>)Delegate.Combine(a, a)",
            "parameter" => "f",
            "field" => "_state.F",
            "two-awaited" or "two-mixed" => A,
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
        var run = AwaitedRunsFixture.Run(work, members: members, fieldDelegate: form == "field");
        var ordered = Array.IndexOf(Forms, form) < 4;
        AwaitedRunsFixture.AssertPairs(run, after: !ordered, self: !ordered);
    }

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
