using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Reporting;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Known calls in the engine and in coverage (R1, R5): a call the library table describes is counted apart from the opaque
/// ones and otherwise treated as the opaque call it was, and a call the table describes for another version stays opaque.</summary>
public sealed class KnownCallCoverageTests
{
    // ---- every other reader of the opaque calls treats a known call as before ----

    [Fact]
    public void Iterator_handed_to_Enumerable_ToList_still_gets_an_unknown_enumeration()
    {
        var run = Run("System.Linq.Enumerable.ToList(_state.Walk());");

        Assert.Contains(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownEnumeration);
        Assert.Equal(1, Delta(run, Run(""), CoverageCounters.KNOWN_CALL));
    }

    [Fact]
    public void Iterator_handed_to_JsonSerializer_Serialize_still_gets_an_unknown_enumeration()
    {
        var run = Run("System.Text.Json.JsonSerializer.Serialize(_state.Walk());");

        Assert.Contains(run.Execution.Analysis.Executions, execution => execution.Kind == ExecutionKind.UnknownEnumeration);
        Assert.Equal(1, Delta(run, Run(""), CoverageCounters.KNOWN_CALL));
    }

    // ---- the counters ----

    [Fact]
    public void Known_call_counts_as_known_and_not_as_opaque()
    {
        var run = Run("System.Text.Json.JsonSerializer.Serialize(_state);");
        var without = Run("");

        Assert.Equal(1, Delta(run, without, CoverageCounters.KNOWN_CALL));
        Assert.Equal(0, Delta(run, without, CoverageCounters.OPAQUE_CALL));
        Assert.Equal(0, Delta(run, without, CoverageCounters.OUT_OF_RANGE_CALL));
    }

    [Fact]
    public void Known_call_is_not_among_the_top_opaque_callees()
    {
        var run = Run("System.Text.Json.JsonSerializer.Serialize(_state); System.Console.WriteLine(_state.Count);");

        Assert.DoesNotContain(run.Collection.Coverage.TopOpaqueCallees, callee => callee.Callee.Contains("JsonSerializer", StringComparison.Ordinal));
        Assert.Contains(run.Collection.Coverage.TopOpaqueCallees, callee => callee.Callee.Contains("Console.WriteLine", StringComparison.Ordinal));
        Assert.Equal(run.Counter(CoverageCounters.OPAQUE_CALL), run.Collection.Coverage.TopOpaqueCallees.Sum(callee => callee.Count));
    }

    [Fact]
    public void Call_out_of_range_counts_as_opaque_and_as_out_of_range()
    {
        var options = new FixtureOptions { MetadataReferences = [StubAssemblies.Get(StubAssemblies.NEWTONSOFT_JSON, 12)] };
        var run = Run("Newtonsoft.Json.JsonConvert.SerializeObject(_state);", options);
        var without = Run("", options);

        Assert.Equal(1, Delta(run, without, CoverageCounters.OPAQUE_CALL));
        Assert.Equal(1, Delta(run, without, CoverageCounters.OUT_OF_RANGE_CALL));
        Assert.Equal(0, Delta(run, without, CoverageCounters.KNOWN_CALL));
        Assert.Contains(run.Collection.Coverage.TopOpaqueCallees, callee => callee.Callee.Contains("SerializeObject", StringComparison.Ordinal));
    }

    [Fact]
    public void Call_the_table_does_not_describe_behaves_as_before()
    {
        var run = Run("System.Console.WriteLine(_state);");
        var without = Run("");

        Assert.Equal(1, Delta(run, without, CoverageCounters.OPAQUE_CALL));
        Assert.Equal(0, Delta(run, without, CoverageCounters.KNOWN_CALL));
        Assert.Equal(0, Delta(run, without, CoverageCounters.OUT_OF_RANGE_CALL));
        Assert.Contains(run.Collection.Coverage.TopOpaqueCallees, callee => callee.Callee.Contains("Console.WriteLine", StringComparison.Ordinal));
    }

    [Fact]
    public void Implicit_object_constructor_is_known()
    {
        var run = Run("var plain = new Plain();", extraTypes: "public sealed class Plain { }");
        var without = Run("", extraTypes: "public sealed class Plain { }");

        Assert.Equal(1, Delta(run, without, CoverageCounters.KNOWN_CALL));
        Assert.Equal(0, Delta(run, without, CoverageCounters.OPAQUE_CALL));
        Assert.DoesNotContain(run.Collection.Coverage.TopOpaqueCallees, callee => callee.Callee == "object..ctor()");
    }

    [Fact]
    public void Linq_operator_with_a_delegate_stays_opaque_and_counts_its_delegate()
    {
        var run = Run("System.Linq.Enumerable.Any(_state.Items, item => item > 0);");
        var without = Run("");

        Assert.Equal(1, Delta(run, without, CoverageCounters.OPAQUE_CALL));
        Assert.Equal(1, Delta(run, without, CoverageCounters.DELEGATE_TO_OPAQUE));
        Assert.Equal(0, Delta(run, without, CoverageCounters.KNOWN_CALL));
    }

    [Fact]
    public void Arguments_of_a_known_call_are_not_unproven_references()
    {
        var known = Run("int.TryParse(_state.Text, out _state.Count);");
        var opaque = Run("int.TryParse(_state.Text, System.Globalization.NumberStyles.Any, null, out _state.Count);");

        Assert.Equal(0, known.Counter(CoverageCounters.UNPROVEN_REFERENCE));
        Assert.Equal(1, opaque.Counter(CoverageCounters.UNPROVEN_REFERENCE));
    }

    // ---- accesses ----

    [Fact]
    public void Known_call_without_effect_gives_no_access()
    {
        var state = "var state = _state; var other = _other;";
        var run = Run(state + " var tick = System.Environment.TickCount; var same = object.ReferenceEquals(state, other);");
        var without = Run(state + " var tick = 0; var same = false;");

        Assert.Equal(2, Delta(run, without, CoverageCounters.KNOWN_CALL));
        Assert.Equal(Shapes(without), Shapes(run));
    }

    [Fact]
    public void Out_argument_write_of_a_known_call_is_kept()
    {
        var run = Run("int.TryParse(_state.Text, out _state.Count);");

        Assert.Equal(1, Delta(run, Run(""), CoverageCounters.KNOWN_CALL));
        Assert.Contains(run.Accesses("Count"), access => access.Operation == AccessOperation.Write && access.Symbol.Contains("Worker", StringComparison.Ordinal));
        var pair = Assert.Single(run.PairsOn("Count"));
        Assert.NotEqual(pair.First.Symbol, pair.Second.Symbol);
    }

    // ---- dispatch to a source body comes first ----

    [Fact]
    public void Source_override_of_a_table_member_runs_its_body_and_is_no_known_call()
    {
        const string Shop = """
            public sealed class Shop : Microsoft.EntityFrameworkCore.DbContext
            {
                public object? Last;
                public override Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry Add(object entity) { Last = entity; return null!; }
            }
            """;
        // Typed as the library's own context, so the call names the member the table describes and only dispatch finds the override.
        var run = Run("Microsoft.EntityFrameworkCore.DbContext shop = new Shop(); shop.Add((object)_state);", Packages, Shop);
        var without = Run("Microsoft.EntityFrameworkCore.DbContext shop = new Shop();", Packages, Shop);

        Assert.Contains(run.Collection.Accesses, access => access.Operation == AccessOperation.Write && access.Symbol.Contains("Shop.Add", StringComparison.Ordinal));
        Assert.Equal(0, Delta(run, without, CoverageCounters.KNOWN_CALL));
        Assert.Equal(0, Delta(run, without, CoverageCounters.OPAQUE_CALL));
    }

    [Fact]
    public void Base_call_from_a_source_override_is_a_known_call()
    {
        const string WithBase = """
            public sealed class Shop : Microsoft.EntityFrameworkCore.DbContext
            {
                public object? Last;
                public override Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry Add(object entity) { Last = entity; return base.Add(entity); }
            }
            """;
        const string WithoutBase = """
            public sealed class Shop : Microsoft.EntityFrameworkCore.DbContext
            {
                public object? Last;
                public override Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry Add(object entity) { Last = entity; return null!; }
            }
            """;
        var run = Run("var shop = new Shop(); shop.Add((object)_state);", Packages, WithBase);
        var without = Run("var shop = new Shop(); shop.Add((object)_state);", Packages, WithoutBase);

        Assert.Equal(1, Delta(run, without, CoverageCounters.KNOWN_CALL));
        Assert.Equal(0, Delta(run, without, CoverageCounters.OPAQUE_CALL));
        Assert.Contains(run.Collection.Accesses, access => access.Operation == AccessOperation.Write && access.Symbol.Contains("Shop.Add", StringComparison.Ordinal));
    }

    [Fact]
    public void Base_property_read_from_a_source_override_is_a_known_call()
    {
        static string Shop(string getter) => $$"""
            public sealed class Shop : Microsoft.EntityFrameworkCore.DbContext
            {
                public override Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Database => {{getter}};
            }
            """;
        var run = Run("var shop = new Shop(); _ = shop.Database;", Packages, Shop("base.Database"));
        var without = Run("var shop = new Shop(); _ = shop.Database;", Packages, Shop("null!"));

        Assert.Equal(1, Delta(run, without, CoverageCounters.KNOWN_CALL));
        Assert.Equal(0, Delta(run, without, CoverageCounters.OPAQUE_CALL));
    }

    [Fact]
    public void Interface_member_of_the_table_with_a_source_implementation_runs_that_implementation()
    {
        const string Registrations = "services.AddSingleton<System.Net.Http.IHttpClientFactory, CountingFactory>(); services.AddHostedService<FactoryWorker>();";
        static string Factory(string call) => $$"""
            public sealed class CountingFactory(State state) : System.Net.Http.IHttpClientFactory
            {
                public System.Net.Http.HttpClient CreateClient(string name) { state.Count++; return null!; }
            }

            public sealed class FactoryWorker(System.Net.Http.IHttpClientFactory factory) : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { {{call}} return Task.CompletedTask; }
            }
            """;
        var run = Run("", Packages, Factory("factory.CreateClient(\"orders\");"), registrations: Registrations);
        var without = Run("", Packages, Factory(""), registrations: Registrations);

        Assert.Contains(run.Accesses("Count"), access => access.Symbol.Contains("CountingFactory.CreateClient", StringComparison.Ordinal));
        Assert.Equal(0, Delta(run, without, CoverageCounters.KNOWN_CALL));
        Assert.Equal(0, Delta(run, without, CoverageCounters.OPAQUE_CALL));
    }

    // ---- the diagnostic and the report ----

    [Fact]
    public async Task Out_of_range_assembly_is_one_diagnostic_per_project_with_its_version_and_range()
    {
        var options = new FixtureOptions
        {
            MetadataReferences = [StubAssemblies.Get(StubAssemblies.NEWTONSOFT_JSON, 12)],
            ProjectOutputKinds = new Dictionary<string, OutputKind> { ["App"] = OutputKind.ConsoleApplication },
            ProjectReferences = [("App", "Library")]
        };
        var solution = FixtureSolution.CreateProjects(options,
            ("Library", "Library.cs", Usings + "public static class Formatter { public static string A(object o) => Newtonsoft.Json.JsonConvert.SerializeObject(o); " +
                                                "public static string B(object o) => Newtonsoft.Json.JsonConvert.SerializeObject(o); }"),
            ("App", "App.cs", Source("_ = Formatter.A(_state); _ = Newtonsoft.Json.JsonConvert.SerializeObject(_state);",
                                     "public static class Program { public static void Main() { } }")));

        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);

        var diagnostics = OutOfRange(result);
        Assert.Equal(2, diagnostics.Length);
        Assert.Single(diagnostics, diagnostic => diagnostic.Contains(" Library: Newtonsoft.Json 12.0.0.0: ", StringComparison.Ordinal));
        Assert.Single(diagnostics, diagnostic => diagnostic.Contains(" App: Newtonsoft.Json 12.0.0.0: ", StringComparison.Ordinal));
        Assert.All(diagnostics, diagnostic => Assert.Contains("outside the supported range 13.0.0.0 up to 14.0.0.0", diagnostic, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Project_two_scopes_share_is_one_diagnostic_in_the_report()
    {
        var options = new FixtureOptions
        {
            MetadataReferences = [StubAssemblies.Get(StubAssemblies.NEWTONSOFT_JSON, 12)],
            ProjectOutputKinds = new Dictionary<string, OutputKind> { ["AppA"] = OutputKind.ConsoleApplication, ["AppB"] = OutputKind.ConsoleApplication },
            ProjectReferences = [("AppA", "Library"), ("AppB", "Library")]
        };
        const string Main = "public static class Program { public static void Main() { _ = Formatter.A(new object()); } }";
        var solution = FixtureSolution.CreateProjects(options,
            ("Library", "Library.cs", Usings + "public static class Formatter { public static string A(object o) => Newtonsoft.Json.JsonConvert.SerializeObject(o); }"),
            ("AppA", "AppA.cs", Usings + Main),
            ("AppB", "AppB.cs", Usings + Main));

        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);

        Assert.Equal(2, result.Scopes.Count);
        Assert.Single(OutOfRange(result), diagnostic => diagnostic.Contains(" Library: Newtonsoft.Json 12.0.0.0: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Projects_of_one_assembly_name_are_each_a_diagnostic()
    {
        var options = new FixtureOptions
        {
            MetadataReferences = [StubAssemblies.Get(StubAssemblies.NEWTONSOFT_JSON, 12)],
            ProjectOutputKinds = new Dictionary<string, OutputKind> { ["AppA"] = OutputKind.ConsoleApplication, ["AppB"] = OutputKind.ConsoleApplication }
        };
        const string Main = "public static class Program { public static void Main() { _ = Newtonsoft.Json.JsonConvert.SerializeObject(new object()); } }";
        var solution = FixtureSolution.CreateProjects(options, ("AppA", "AppA.cs", Usings + Main), ("AppB", "AppB.cs", Usings + Main));
        foreach (var project in solution.Projects.ToArray())
            solution = solution.WithProjectAssemblyName(project.Id, "SameAssembly");

        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);

        var diagnostics = OutOfRange(result);
        Assert.Equal(2, diagnostics.Length);
        Assert.Single(diagnostics, diagnostic => diagnostic.Contains(" AppA: Newtonsoft.Json 12.0.0.0: ", StringComparison.Ordinal));
        Assert.Single(diagnostics, diagnostic => diagnostic.Contains(" AppB: Newtonsoft.Json 12.0.0.0: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_diagnostic_in_range_or_without_a_reference()
    {
        var inRange = FixtureSolution.Create(new FixtureOptions { MetadataReferences = Packages.MetadataReferences },
                                             ("Case.cs", Source("_ = Newtonsoft.Json.JsonConvert.SerializeObject(_state);")));
        var unreferenced = FixtureSolution.Create(("Case.cs", Source("_ = System.Text.Json.JsonSerializer.Serialize(_state);")));

        Assert.Empty(OutOfRange(await PhaseOneAnalyzer.AnalyzeAsync(inRange, ROOT_DIRECTORY, CancellationToken.None)));
        Assert.Empty(OutOfRange(await PhaseOneAnalyzer.AnalyzeAsync(unreferenced, ROOT_DIRECTORY, CancellationToken.None)));
    }

    [Fact]
    public async Task Report_explains_both_counters()
    {
        var solution = FixtureSolution.Create(("Case.cs", Source("System.Text.Json.JsonSerializer.Serialize(_state);")));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, CancellationToken.None);
        var lines = ReportRenderer.Render(ReportingTestData.CreateReport(result)).ReportMarkdown.Split('\n');

        var known = Assert.Single(lines, line => line.StartsWith($"  - {CoverageCounters.KNOWN_CALL} ", StringComparison.Ordinal));
        Assert.Matches(@"^  - known-call [1-9]\d*: calls without a source body that the library semantics table describes", known);
        Assert.Matches(@"^  - out-of-range-call 0: opaque calls of a member the table describes, in an assembly version outside its supported range$",
                       Assert.Single(lines, line => line.StartsWith($"  - {CoverageCounters.OUT_OF_RANGE_CALL} ", StringComparison.Ordinal)));
    }

    // ---- helpers ----

    /// <summary>The real packages beside the stubs, for the cases over EF Core, the logging abstractions or the client factory.</summary>
    private static readonly FixtureOptions Packages = new() { MetadataReferences = LibraryPackages.BesideStubs };

    private static int Delta(EngineRun run, EngineRun without, string counter) => run.Counter(counter) - without.Counter(counter);

    /// <summary>Every access as what it touches and how, in order: two runs whose calls add nothing have the same shapes.</summary>
    private static string[] Shapes(EngineRun run) =>
        run.Collection.Accesses.Select(access => $"{access.Symbol} {access.Operation} {access.Resource.Region}.{string.Join('.', access.Resource.AccessPath)}")
           .Order(StringComparer.Ordinal)
           .ToArray();

    private static string[] OutOfRange(AnalysisResult result) =>
        result.Coverage.SelectMany(coverage => coverage.Diagnostics)
              .Where(diagnostic => diagnostic.StartsWith("library-semantics: ", StringComparison.Ordinal))
              .ToArray();

    private static EngineRun Run(string work, FixtureOptions? options = null, string extraTypes = "", string registrations = "") =>
        AnalyzeScope(FixtureSolution.Create(options ?? new FixtureOptions(), ("Case.cs", Source(work, extraTypes, registrations))), "scope:Fixture");

    /// <summary>A singleton <c>State</c> that one worker does <paramref name="work"/> on while another writes its count.</summary>
    private static string Source(string work, string extraTypes = "", string registrations = "") => Usings + $$"""
        using System.Collections.Generic;

        public sealed class State
        {
            public int Count;
            public string Text = "";
            public List<int> Items = new();
            public IEnumerable<int> Walk() { Count = 1; yield return 1; }
        }

        public sealed class Other { }

        public sealed class Worker : BackgroundService
        {
            private readonly State _state;
            private readonly Other _other;
            public Worker(State state, Other other)
            {
                _state = state;
                _other = other;
            }

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{work}}
                return Task.CompletedTask;
            }
        }

        public sealed class Reader : BackgroundService
        {
            private readonly State _state;
            public Reader(State state) => _state = state;

            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                _state.Count = 2;
                return Task.CompletedTask;
            }
        }

        {{extraTypes}}
        """ + Startup("services.AddSingleton<State>(); services.AddSingleton<Other>(); services.AddHostedService<Worker>(); services.AddHostedService<Reader>(); " +
                      registrations);
}
