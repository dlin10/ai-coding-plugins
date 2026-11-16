using System.Text.Json;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class CallAdmissionMatrixTests
{
    private const string ROOT_DIRECTORY = @"C:\fixture";
    private const string LIBRARY = "AdmissionLibrary";
    private const string FOREIGN_LIBRARY = "OtherLibrary";
    private enum Origin { AdmittedCall, SameBody, SameSpan, OtherBody, LibraryBody, None }
    private enum Target { AdmittedMember, OtherMember, OtherLibrary }
    private sealed record Cell(AdmissionRoute Route, Origin Origin, Target Target);
    private sealed record Edge(string? BodyId, int OperationId);
    private sealed record LoweredCall(IrBody Body, IrCallOperation Call);

    private static IEnumerable<Cell> Cells() =>
        from route in Enum.GetValues<AdmissionRoute>()
        from origin in Enum.GetValues<Origin>()
        where origin == Origin.None
            ? route is AdmissionRoute.Root or AdmissionRoute.Startup or AdmissionRoute.Di or AdmissionRoute.TypeInitializer
            : route is not (AdmissionRoute.Root or AdmissionRoute.Startup or AdmissionRoute.Di)
        from target in Enum.GetValues<Target>()
        select new Cell(route, origin, target);

    private static bool Enters(Cell cell) =>
        cell.Origin == Origin.LibraryBody && cell.Target != Target.OtherLibrary ||
        cell.Origin == Origin.AdmittedCall && cell.Route == AdmissionRoute.Direct && cell.Target == Target.AdmittedMember;

    [Fact]
    public void Every_value_of_every_axis_occurs_and_the_cells_are_counted()
    {
        var cells = Cells().ToArray();
        Assert.Equal(117, cells.Length);
        Assert.Equal(cells.Length, cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<AdmissionRoute>(), cells.Select(cell => cell.Route).Distinct().Order());
        Assert.Equal(Enum.GetValues<Origin>(), cells.Select(cell => cell.Origin).Distinct().Order());
        Assert.Equal(Enum.GetValues<Target>(), cells.Select(cell => cell.Target).Distinct().Order());
        Assert.Equal(12, cells.Count(cell => cell.Origin == Origin.None));
        Assert.All(cells.Where(cell => cell.Route is AdmissionRoute.Root or AdmissionRoute.Startup or AdmissionRoute.Di),
                   cell => Assert.Equal(Origin.None, cell.Origin));
        Assert.All(cells.Where(cell => cell.Origin == Origin.None),
                   cell => Assert.Contains(cell.Route, new[] { AdmissionRoute.Root, AdmissionRoute.Startup, AdmissionRoute.Di, AdmissionRoute.TypeInitializer }));
    }

    [Fact]
    public void Every_cell_is_exact()
    {
        foreach (var group in Cells().GroupBy(cell => new { Dispatch = cell.Route == AdmissionRoute.Dispatch, SameSpan = cell.Origin == Origin.SameSpan, cell.Target }))
        {
            var fixture = new AdmissionFixture(group.Key.Dispatch, group.Key.SameSpan, group.Key.Target);
            foreach (var cell in group)
            {
                var edge = fixture.Edge(cell.Origin);
                Assert.True(fixture.Reachable.HasBody(fixture.TargetMethod, edge.BodyId, cell.Route, edge.OperationId) == Enters(cell),
                            $"{cell}: the edge's entry disagrees with admission and indexed body availability");
            }
        }
    }

    [Fact]
    public void A_call_that_does_not_enter_keeps_its_lowering()
    {
        var json = new JsonSerializerOptions { IgnoreReadOnlyProperties = true };
        var cells = Cells().Where(cell => cell.Origin is Origin.AdmittedCall or Origin.SameBody or Origin.SameSpan or Origin.OtherBody &&
                                         cell.Route is AdmissionRoute.Direct or AdmissionRoute.Dispatch).ToArray();
        Assert.Equal(24, cells.Length);
        foreach (var group in cells.GroupBy(cell => new { Dispatch = cell.Route == AdmissionRoute.Dispatch, SameSpan = cell.Origin == Origin.SameSpan, cell.Target }))
        {
            var fixture = new AdmissionFixture(group.Key.Dispatch, group.Key.SameSpan, group.Key.Target);
            foreach (var cell in group)
            {
                var before = fixture.Call(cell.Origin, admitted: false);
                var after = fixture.Call(cell.Origin, admitted: true);
                var edge = fixture.Edge(cell.Origin);
                var enters = fixture.Reachable.HasBody(fixture.TargetMethod, edge.BodyId, cell.Route, edge.OperationId);
                Assert.Equal(Enters(cell), enters);
                Assert.Equal(cell.Route == AdmissionRoute.Dispatch, after.Call.CallKind is IrCallKind.Virtual or IrCallKind.Interface);
                Assert.NotNull(before.Call.Library);
                Assert.True((after.Call.Library is null) == enters, $"{cell}: library annotation");
                if (!enters)
                    Assert.Equal(JsonSerializer.Serialize(before.Call.Library, json), JsonSerializer.Serialize(after.Call.Library, json));
                // A setter has no out argument; the other origins exercise a real out store for each target and route.
                Assert.Equal(group.Key.SameSpan ? 0 : 1, OpaqueOuts(before).Length);
                Assert.Equal(group.Key.SameSpan || enters ? 0 : 1, OpaqueOuts(after).Length);
                if (!enters)
                    Assert.Equal(OpaqueOuts(before).Select(store => store.Provenance), OpaqueOuts(after).Select(store => store.Provenance));
                Assert.Equal(before.Body.CallIdentities[before.Call.Id], after.Body.CallIdentities[after.Call.Id]);
            }
        }
    }

    private static IrStoreReferenceOperation[] OpaqueOuts(LoweredCall call) => call.Body.Blocks.SelectMany(block => block.Operations)
        .SkipWhile(operation => operation.Id != call.Call.Id).Skip(1).TakeWhile(operation => operation is not IrCallOperation)
        .OfType<IrStoreReferenceOperation>().Where(store => store.IsUnseenOutput && store.AddressValue == call.Call.ArgumentAt(0)).ToArray();

    private sealed class AdmissionFixture
    {
        private readonly IReadOnlyDictionary<string, IrBody> _before;
        private readonly IReadOnlyDictionary<string, IrBody> _after;
        private readonly CallIdentity _selected;
        private readonly string _member;
        internal ReachableSetResult Reachable { get; }
        internal ProgramMethod TargetMethod { get; }

        internal AdmissionFixture(bool dispatch, bool sameSpan, Target target)
        {
            var modifier = dispatch ? "virtual " : string.Empty;
            string Type(string name) => "public class " + name + " { public " + modifier +
                "void Put(out object value) { value = new object(); } public " + modifier + "object Value { set { } } }";
            var library = Compile(LIBRARY, Type("One") + Type("Two"));
            var foreign = Compile(FOREIGN_LIBRARY, Type("Far"));
            var references = new[] { Reference(library), Reference(foreign) };
            var work = sameSpan ? "(value.Value, value.Value) = (new object(), new object());" :
                                 "value.Put(out object first); value.Put(out object second);";
            var other = sameSpan ? "value.Value = new object();" : "value.Put(out object result);";
            string Source(string type) => "public class Caller : Far { public static void Work() { " + type + " value = null!; " + work +
                " } public static void Other() { " + type + " value = null!; " + other + " } }";
            Compilation Project(string type) => FixtureSolution.Create(new FixtureOptions { MetadataReferences = references },
                ("Case.cs", Source(type))).Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
            var models = new LibraryModels(new[] { (LIBRARY, "One"), (LIBRARY, "Two"), (FOREIGN_LIBRARY, "Far") }.SelectMany(pair =>
                new[] { "M:" + pair.Item2 + ".Put(System.Object@)", "M:" + pair.Item2 + ".set_Value(System.Object)" }.Select(member =>
                    new LibraryModel(member, [new SupportedAssemblyVersion(pair.Item1, new Version(0, 0, 0, 0), new Version(1, 0, 0, 0))], [])
                    {
                        Outputs = member.Contains(".Put", StringComparison.Ordinal)
                            ? new Dictionary<string, LibraryResult> { ["value"] = new(LibraryResultKind.New, []) } : new Dictionary<string, LibraryResult>()
                    })), []);
            _member = sameSpan ? ".set_Value(System.Object)" : ".Put(System.Object@)";
            var baseline = Project("One");
            var selectedBody = Lower(baseline, "Work", models, null);
            _selected = selectedBody.CallIdentities[Calls(selectedBody).First().Id];
            var admission = new CallSiteAdmission(new Dictionary<CallIdentity, string> { [_selected] = _selected.Target });
            var type = target switch { Target.AdmittedMember => "One", Target.OtherMember => "Two", _ => "Far" };
            var project = target == Target.AdmittedMember ? baseline : Project(type);
            var index = ProgramIndexBuilder.Build("matrix", [project, library], ROOT_DIRECTORY, CancellationToken.None);
            var targetId = $"body:{(target == Target.OtherLibrary ? FOREIGN_LIBRARY : LIBRARY)}:M:{type}{_member}";
            TargetMethod = index.Method(targetId)!;
            Assert.NotNull(TargetMethod);
            Assert.Equal(target != Target.OtherLibrary, TargetMethod.HasSourceBody);
            _before = new[] { "Work", "Other" }.Select(name => Lower(project, name, models, null)).ToDictionary(body => body.BodyId);
            _after = new[] { "Work", "Other" }.Select(name => Lower(project, name, models, admission)).ToDictionary(body => body.BodyId);
            var bodies = new Dictionary<string, IrBody>(_after);
            foreach (var method in index.Methods.Where(method => method.HasSourceBody && method.MethodId.StartsWith($"body:{LIBRARY}:", StringComparison.Ordinal)))
            {
                var symbol = (IMethodSymbol)DocumentationCommentId.GetFirstSymbolForDeclarationId(method.MethodId[($"body:{LIBRARY}:").Length..], library)!;
                bodies.Add(method.MethodId, IrLowering.Lower(symbol, library, ROOT_DIRECTORY, CancellationToken.None, models, admission: admission).Body);
            }
            Reachable = new ReachableSetResult([], new Dictionary<string, string>(), bodies, [], [], [],
                new Dictionary<string, IReadOnlyList<OpaqueCall>>(), []) { Rules = ReachabilityRules.ClassHierarchy with { Admission = admission } };
            Assert.Equal(target != Target.OtherLibrary, Reachable.HasBody(TargetMethod));
            Assert.DoesNotContain(bodies.Keys, id => id.StartsWith($"body:{FOREIGN_LIBRARY}:", StringComparison.Ordinal));
            if (sameSpan)
            {
                var calls = Calls(_before["body:Fixture:M:Caller.Work"]).ToArray();
                Assert.Equal(2, calls.Length);
                var first = _before["body:Fixture:M:Caller.Work"].CallIdentities[calls[0].Id];
                var second = _before["body:Fixture:M:Caller.Work"].CallIdentities[calls[1].Id];
                Assert.Equal(first.Span, second.Span);
                Assert.Equal(first.Target, second.Target);
                Assert.Equal(0, first.Ordinal);
                Assert.Equal(1, second.Ordinal);
            }
        }

        internal Edge Edge(Origin origin) => origin switch
        {
            Origin.None => new(null, -1),
            Origin.LibraryBody => new(_selected.Target, -1),
            _ => new(Call(origin, admitted: true).Body.BodyId, Call(origin, admitted: true).Call.Id)
        };

        internal LoweredCall Call(Origin origin, bool admitted)
        {
            var body = (admitted ? _after : _before)[origin == Origin.OtherBody ? "body:Fixture:M:Caller.Other" : "body:Fixture:M:Caller.Work"];
            return new(body, Calls(body).ElementAt(origin is Origin.SameBody or Origin.SameSpan ? 1 : 0));
        }

        private IEnumerable<IrCallOperation> Calls(IrBody body) => body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
            .Where(call => call.TargetMethodId?.EndsWith(_member, StringComparison.Ordinal) == true).OrderBy(call => call.Id);

        private static IrBody Lower(Compilation project, string member, LibraryModels models, CallSiteAdmission? admission) =>
            IrLowering.Lower(project.GetTypeByMetadataName("Caller")!.GetMembers(member).OfType<IMethodSymbol>().Single(),
                             project, ROOT_DIRECTORY, CancellationToken.None, models, admission: admission).Body;

        private static CSharpCompilation Compile(string assembly, string source) =>
            ((CSharpCompilation)FixtureSolution.Create(("Library.cs", source)).Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!)
                .WithAssemblyName(assembly);

        private static MetadataReference Reference(Compilation compilation)
        {
            using var pe = new MemoryStream();
            var emitted = compilation.Emit(pe);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            return MetadataReference.CreateFromImage(pe.ToArray());
        }
    }
}
