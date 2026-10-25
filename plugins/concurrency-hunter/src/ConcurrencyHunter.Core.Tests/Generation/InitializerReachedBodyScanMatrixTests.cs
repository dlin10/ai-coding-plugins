using System.Collections.Concurrent;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

public sealed partial class InitializerReachedBodyScanMatrixTests
{
    private enum Use { Ref, In, Out, StructReceiver, ByValue, TaskRun }
    private enum Callee { NoBody, UnresolvedTarget }
    private enum Site { Initializer, Helper }
    private enum HelperAlsoReachedBy { Nothing, Setup, Action, Recipe }
    private enum Owner { Library, Driver }
    private enum Route { DirectCall, DelegateCreatedInInitializer, DelegateCreatedInSetup }
    private enum FieldType { Scalar, Struct }
    private sealed record Cell(Use Use, Callee Callee, Site Site, HelperAlsoReachedBy Also, Owner Owner, Route Route, FieldType FieldType);

    private static IEnumerable<Cell> Cells() =>
        (from use in Enum.GetValues<Use>() from callee in Enum.GetValues<Callee>() from site in Enum.GetValues<Site>()
         from also in Enum.GetValues<HelperAlsoReachedBy>() from owner in Enum.GetValues<Owner>() from route in Enum.GetValues<Route>()
         from field in Enum.GetValues<FieldType>() select new Cell(use, callee, site, also, owner, route, field))
        .Where(cell => Allowed(cell) is null);

    private static string? Allowed(Cell cell)
    {
        if (cell.Site == Site.Initializer && cell.Also != HelperAlsoReachedBy.Nothing)
            return "The initializer is not a helper shared with another action.";
        if (cell.Route != Route.DirectCall && (cell.Site == Site.Initializer || cell.Also != HelperAlsoReachedBy.Nothing))
            return "Delegate routes isolate the helper reached only from the initializer.";
        if (cell.Use == Use.TaskRun && (cell.Callee != Callee.NoBody || cell.Owner != Owner.Library || cell.FieldType != FieldType.Scalar))
            return "Work does not hand storage to an unseen callee and uses one representative field.";
        if (cell.Use == Use.StructReceiver && cell.FieldType == FieldType.Scalar)
            return "A scalar has no struct receiver method.";
        return null;
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        var cells = Cells().ToArray();
        Assert.Equal(Enum.GetValues<Use>(), cells.Select(cell => cell.Use).Distinct().Order());
        Assert.Equal(Enum.GetValues<Callee>(), cells.Select(cell => cell.Callee).Distinct().Order());
        Assert.Equal(Enum.GetValues<Site>(), cells.Select(cell => cell.Site).Distinct().Order());
        Assert.Equal(Enum.GetValues<HelperAlsoReachedBy>(), cells.Select(cell => cell.Also).Distinct().Order());
        Assert.Equal(Enum.GetValues<Owner>(), cells.Select(cell => cell.Owner).Distinct().Order());
        Assert.Equal(Enum.GetValues<Route>(), cells.Select(cell => cell.Route).Distinct().Order());
        Assert.Equal(Enum.GetValues<FieldType>(), cells.Select(cell => cell.FieldType).Distinct().Order());
    }

    [Fact]
    public void Every_cell_gets_its_expected_answer()
    {
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(Cells(), CellParallelism, cell =>
        {
            try
            {
                var trace = Generate(cell);
                Assert.Equal("off", trace.Answer.Generation.Mode);
                Assert.NotNull(trace.Run);
                var state = cell.Owner == Owner.Library && cell.Use is Use.Ref or Use.In or Use.Out or Use.StructReceiver;
                var work = cell.Use == Use.TaskRun;
                Assert.True(state == trace.Answer.Causes.Any(cause => cause.Code == ModelCauses.LIBRARY_STATE_STORE),
                            string.Join("; ", trace.Answer.Causes));
                Assert.Equal(work, trace.Answer.Causes.Any(cause => cause.Code == ModelCauses.INITIALIZER_WORK_NOT_RUN));
                if (state)
                {
                    Assert.Null(trace.Answer.Model);
                    Assert.Equal(ModelReasons.LIBRARY_STATE, trace.Answer.ModelReason);
                    var cause = Assert.Single(trace.Answer.Causes, cause => cause.Code == ModelCauses.LIBRARY_STATE_STORE);
                    var storage = cell.Use == Use.StructReceiver && cell.Callee == Callee.UnresolvedTarget ? "Storage" : "Api";
                    Assert.Contains($"static:{ASSEMBLY}:Lib.{storage}", cause.Detail);
                    Assert.Contains("Init.#cctor", cause.Detail);
                    Assert.Contains(trace.StateStores, store => store.Region.StartsWith($"static:{ASSEMBLY}:Lib.{storage}", StringComparison.Ordinal));
                }
                if (work)
                {
                    Assert.Equal(ModelReasons.INCOMPLETE, trace.Answer.ModelReason);
                    Assert.Contains("Init.#cctor", Assert.Single(trace.Answer.Causes, cause => cause.Code == ModelCauses.INITIALIZER_WORK_NOT_RUN).Detail);
                }
            }
            catch (Exception error)
            {
                failures.Add($"{cell}: {error}");
            }
        });
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Order()));
    }

    private static GenerationTrace Generate(Cell cell)
    {
        var type = cell.FieldType == FieldType.Scalar ? "int" : "S";
        var place = cell.Owner == Owner.Library ? "X" : "ModelDriver.Out_x_Call";
        var argument = cell.Use switch { Use.Ref => "ref ", Use.In => "in ", Use.Out => "out ", _ => "" };
        var hand = cell.Use switch
        {
            Use.TaskRun => "_ = Task.Run(() => Count++);",
            Use.StructReceiver when cell.Callee == Callee.NoBody => place + ".Touch();",
            Use.StructReceiver => "Dispatch.Go(ref " + place + ");",
            _ => (cell.Callee == Callee.NoBody ? "Ext." : "Dispatch.") + cell.Use + "(" +
                 (cell.Callee == Callee.NoBody ? "" : "new R(), ") + argument + place + ");"
        };
        if (cell.Callee == Callee.UnresolvedTarget)
        {
            // The storage is named at the unresolved constrained call itself, with no alias or ref-parameter wrapper.
            hand = cell.Use == Use.StructReceiver
                ? "void Send<T>() where T : struct, IGo { " + (cell.Owner == Owner.Library ? "Storage<T>" : "DriverStorage<T>") +
                  ".X.Go(); } Send<S>();"
                : "void Send<T>(T r) where T : struct, IRun { r." + cell.Use + "(" + argument + place + "); } Send(new R());";
        }
        var fromInitializer = cell.Site == Site.Initializer ? hand : cell.Route switch
        {
            Route.DirectCall => "Helper();",
            Route.DelegateCreatedInInitializer => "Action f = Helper; f();",
            _ => "Hooks.F();"
        };
        var declarations = $$"""
            public interface IGo { void Go(); }
            public struct S : IGo { public int Value; public extern void Touch(); public void Go() { Value++; } }
            public interface IRun { void Ref(ref {{type}} x); void In(in {{type}} x); void Out(out {{type}} x); void ByValue({{type}} x); }
            public struct R : IRun
            {
                public void Ref(ref {{type}} x) { } public void In(in {{type}} x) { }
                public void Out(out {{type}} x) { x = default; } public void ByValue({{type}} x) { }
            }
            public static class Ext
            {
                public static extern void Ref(ref {{type}} x); public static extern void In(in {{type}} x);
                public static extern void Out(out {{type}} x); public static extern void ByValue({{type}} x);
            }
            public static class Dispatch
            {
                public static void Ref<T>(T r, ref {{type}} x) where T : struct, IRun => r.Ref(ref x);
                public static void In<T>(T r, in {{type}} x) where T : struct, IRun => r.In(in x);
                public static void Out<T>(T r, out {{type}} x) where T : struct, IRun => r.Out(out x);
                public static void ByValue<T>(T r, {{type}} x) where T : struct, IRun => r.ByValue(x);
                public static void Go<T>(ref T x) where T : struct, IGo => x.Go();
            }
            public static class Hooks { public static Action F; }
            public static class Storage<T> where T : struct { public static T X; }
            public sealed class RecipeValue { public RecipeValue() { Api.RecipeHelper(); } }
            public sealed class Api
            {
                public static {{type}} X; public static int Count;
                public static RecipeValue Recipe;
                public Api() { {{(cell.Route == Route.DelegateCreatedInSetup ? "Hooks.F = Helper;" : "")}}
                               {{(cell.Also == HelperAlsoReachedBy.Setup ? "Helper();" : "")}} }
                private static class Init { static Init() { {{fromInitializer}} } public static void Touch() { } }
                private static void Helper() { {{(cell.Site == Site.Helper ? hand : "")}} }
                public static void RecipeHelper() { {{(cell.Also == HelperAlsoReachedBy.Recipe ? "Helper();" : "")}} }
                public void Run({{(cell.Owner == Owner.Driver ? "ref " + type + " x" : "")}}) { Init.Touch(); {{(cell.Also == HelperAlsoReachedBy.Action ? "Helper();" : "")}} {{(cell.Also == HelperAlsoReachedBy.Recipe ? "_ = Recipe;" : "")}} }
            }
            """;
        var references = EmittedAssemblies.RuntimeReferences.AsEnumerable();
        if (cell.Owner == Owner.Driver)
        {
            // The library can name the driver's storage through a metadata reference; its bodies are still generated by Trace.
            // Scalar cells need no library type in that reference. Struct cells use a struct declared in the driver assembly.
            var driverSource = "public static class ModelDriver { public static " +
                               (cell.FieldType == FieldType.Scalar ? "int" : "Lib.S") + " Out_x_Call; } " +
                               "public static class DriverStorage<T> where T : struct { public static T X; }";
            if (cell.FieldType == FieldType.Struct)
            {
                driverSource += " namespace Lib { public interface IGo { void Go(); } public struct S : IGo { public int Value; public extern void Touch(); public void Go() { Value++; } } }";
                declarations = declarations.Replace("public interface IGo { void Go(); }", "")
                    .Replace("public struct S : IGo { public int Value; public extern void Touch(); public void Go() { Value++; } }", "");
            }
            var driver = CSharpCompilation.Create(DriverSynthesizer.ASSEMBLY,
                                                  [CSharpSyntaxTree.ParseText(driverSource, path: "DriverFixture/State.cs")],
                                                  references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.DoesNotContain(driver.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            references = references.Append(driver.ToMetadataReference());
        }
        var library = LibraryCompilation.CompileTrees(ASSEMBLY,
            [CSharpSyntaxTree.ParseText(PRELUDE + declarations + "}", new CSharpParseOptions(LanguageVersion.Preview), ASSEMBLY + "/Library.cs")],
            references.ToArray(), CancellationToken.None);
        Assert.NotNull(library.Compilation);
        var member = "M:Lib.Api.Run" + (cell.Owner == Owner.Driver ? "(" + (cell.FieldType == FieldType.Scalar ? "System.Int32" : "Lib.S") + "@)" : "");
        return ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", member, null, null), library,
                                    CancellationToken.None, initializers: TypeInitializerTreatment.Off);
    }
}
