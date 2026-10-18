using System.Security.Cryptography;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The generated layer in <see cref="LibraryModels"/> (R1, R2, R5): a call is described by an entry of the file its own
/// target names, that applies in the call's own compilation, and only when no project entry and no built-in entry in range describes it.</summary>
public sealed class GeneratedEntryApplicabilityTests : IDisposable
{
    private const string PACKAGE = "Acme.Lib";
    private const string LIBRARY = """
        namespace Acme
        {
            public static class Api
            {
                public static void Touch(object value) { }
                public static int Compute(int value) => value;
                public static string Name => "";
            }

            public static class Ext { public static int Twice(this int value) => value * 2; }
        }
        """;

    private const string DEPENDENT_LIBRARY = "namespace Acme { public static class Api { public static void Fill(Dep.Item item, Dep.Base[] bag) { } } }";
    private const string ORDERING_LIBRARY = "namespace Acme { public static class Api { public static void Order(Dep.Item left, Dep.Item right) { } } }";

    // Two spellings of one family of ids: Roslyn writes nothing for a function-pointer type, so `F(delegate*<void>)` has the id of `F()`,
    // which it resolves to the parameterless `F` whose own id is `M:Acme.Api.F`.
    private const string ALIASED_LIBRARY = """
        namespace Acme
        {
            public static unsafe class Api
            {
                public static void F() { }
                public static void F(delegate*<void> callback) { }
                public static int Compute(int value) => value;
            }
        }
        """;
    private const string DRIVER = "public static class ModelDriver { public static void V_Call() { Acme.Api.Touch(new object()); } }";

    private readonly GenerationInstall _install = new();

    public void Dispose() => _install.Dispose();

    private string Repository => Path.Combine(_install.Root, "repository");

    [Fact]
    public void Generated_entry_describes_the_call_as_known_generated()
    {
        _install.SharedFramework("8.0.5");
        var image = Library("one");
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, image);
        var project = Project(DriverSynthesizer.ASSEMBLY, 8, "1.0.0", "net8.0", image, DRIVER);
        var touch = Member(project, "Api", "Touch");
        var target = TargetOf(project, touch);
        var table = new GeneratedModelTable();
        table.MapType(project, touch.ContainingType, target.Key);
        Assert.Empty(table.AddFile(Stored(target.Key, GeneratedModelAnswer.OfModel(Model(touch, LibraryEffect.DeepReadOf("value"))))));
        var models = LibraryModels.BuiltIn.WithGenerated(table);

        var match = models.Find(touch, project);

        Assert.NotNull(match);
        Assert.Equal((LibraryMatchKind.Known, ModelLayer.Generated, false), (match.Kind, match.Layer, match.DeclaredOpaque));
        Assert.Equal(touch.GetDocumentationCommentId(), match.MemberId);
        Assert.Equal([LibraryEffect.DeepReadOf("value")], match.Effects);
        // The layer is kept per compilation: without the layer, or without the call's compilation, the member is an opaque call.
        Assert.Null(LibraryModels.BuiltIn.Find(touch, project));
        Assert.Null(models.Find(touch));
        Assert.Null(models.Find(touch, Project("Other", 8, "1.0.0", "net8.0", image)));

        // Through the whole pipeline: the call is counted as known by the generated layer and by nothing else.
        var without = Pipeline(project, LibraryModels.BuiltIn).Collection!.Coverage.Counters;
        var with = Pipeline(project, models).Collection!.Coverage.Counters;
        Assert.Equal((0, 1), (without[CoverageCounters.KNOWN_CALL_GENERATED], with[CoverageCounters.KNOWN_CALL_GENERATED]));
        Assert.Equal(without[CoverageCounters.OPAQUE_CALL] - 1, with[CoverageCounters.OPAQUE_CALL]);
        Assert.Equal(without[CoverageCounters.KNOWN_CALL_BUILT_IN], with[CoverageCounters.KNOWN_CALL_BUILT_IN]);
        Assert.Equal(without[CoverageCounters.KNOWN_CALL_PROJECT], with[CoverageCounters.KNOWN_CALL_PROJECT]);
        Assert.All(new[] { without, with }, counters => Assert.Equal(counters[CoverageCounters.KNOWN_CALL_BUILT_IN] + counters[CoverageCounters.KNOWN_CALL_PROJECT] +
                                                                     counters[CoverageCounters.KNOWN_CALL_GENERATED], counters[CoverageCounters.KNOWN_CALL]));
    }

    [Fact]
    public void Entry_of_another_mvid_is_not_applied()
    {
        _install.SharedFramework("8.0.5");
        // One assembly version, two package versions, two MVIDs.
        var older = Library("older");
        var newer = Library("newer");
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, older);
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.1", "net8.0", PACKAGE, newer);
        var scopeOlder = Project("Older", 8, "1.0.0", "net8.0", older);
        var scopeNewer = Project("Newer", 8, "1.0.1", "net8.0", newer);
        var (touchOlder, touchNewer) = (Member(scopeOlder, "Api", "Touch"), Member(scopeNewer, "Api", "Touch"));
        var (keyOlder, keyNewer) = (TargetOf(scopeOlder, touchOlder).Key, TargetOf(scopeNewer, touchNewer).Key);
        Assert.NotEqual(keyOlder.Mvid, keyNewer.Mvid);
        Assert.Equal(keyOlder.AssemblyVersion, keyNewer.AssemblyVersion);
        var fileOlder = Stored(keyOlder, GeneratedModelAnswer.OfModel(Model(touchOlder, LibraryEffect.DeepReadOf("value"))));
        var fileNewer = Stored(keyNewer, GeneratedModelAnswer.OfModel(Model(touchNewer)));

        // The scope references the newer asset: the older asset's file, though it holds a valid entry for the member, is not its file.
        var onlyOlder = new GeneratedModelTable();
        onlyOlder.MapType(scopeNewer, touchNewer.ContainingType, keyNewer);
        Assert.Empty(onlyOlder.AddFile(fileOlder));
        Assert.Null(LibraryModels.BuiltIn.WithGenerated(onlyOlder).Find(touchNewer, scopeNewer));

        foreach (var files in new[] { new[] { fileOlder, fileNewer }, [fileNewer, fileOlder] })
        {
            var table = new GeneratedModelTable();
            table.MapType(scopeNewer, touchNewer.ContainingType, keyNewer);
            Assert.Empty(files.SelectMany(table.AddFile));
            var match = LibraryModels.BuiltIn.WithGenerated(table).Find(touchNewer, scopeNewer);
            Assert.Equal((LibraryMatchKind.Known, ModelLayer.Generated), (match!.Kind, match.Layer));
            Assert.Empty(match.Effects);
        }
    }

    [Fact]
    public void Scopes_apply_only_their_own_files()
    {
        _install.SharedFramework("8.0.5");
        var (first, second) = (Library("first"), Library("second"));
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, first);
        ReferenceScopes.AddAsset(_install, PACKAGE, "2.0.0", "net8.0", PACKAGE, second);
        var (inFirst, inSecond) = (Project("First", 8, "1.0.0", "net8.0", first), Project("Second", 8, "2.0.0", "net8.0", second));
        var (touchFirst, touchSecond) = (Member(inFirst, "Api", "Touch"), Member(inSecond, "Api", "Touch"));
        var (keyFirst, keySecond) = (TargetOf(inFirst, touchFirst).Key, TargetOf(inSecond, touchSecond).Key);
        var (fileFirst, fileSecond) = (Stored(keyFirst, GeneratedModelAnswer.OfModel(Model(touchFirst, LibraryEffect.DeepReadOf("value")))),
                                       Stored(keySecond, GeneratedModelAnswer.OfModel(Model(touchSecond))));
        var (tableFirst, tableSecond) = (new GeneratedModelTable(), new GeneratedModelTable());
        tableFirst.MapType(inFirst, touchFirst.ContainingType, keyFirst);
        tableSecond.MapType(inSecond, touchSecond.ContainingType, keySecond);
        Assert.Empty(tableFirst.AddFile(fileFirst));
        Assert.Empty(tableSecond.AddFile(fileSecond));
        var (modelsFirst, modelsSecond) = (LibraryModels.BuiltIn.WithGenerated(tableFirst), LibraryModels.BuiltIn.WithGenerated(tableSecond));

        Assert.Equal([LibraryEffect.DeepReadOf("value")], modelsFirst.Find(touchFirst, inFirst)!.Effects);
        Assert.Empty(modelsSecond.Find(touchSecond, inSecond)!.Effects);
        // A scope's layer knows its own compilations: the other scope's call is an opaque call there.
        Assert.Null(modelsFirst.Find(touchSecond, inSecond));
        Assert.Null(modelsSecond.Find(touchFirst, inFirst));
        // A scope whose target names a file it was not given has no answer for it, though the other scope has.
        var unfed = new GeneratedModelTable();
        unfed.MapType(inFirst, touchFirst.ContainingType, keyFirst);
        Assert.Null(LibraryModels.BuiltIn.WithGenerated(unfed).Find(touchFirst, inFirst));
    }

    [Fact]
    public void Two_projects_of_one_scope_with_two_assets_each_use_their_own_entry()
    {
        _install.SharedFramework("6.0.5");
        _install.SharedFramework("8.0.5");
        // One package version, one assembly version, two assets: the net6.0 and the net8.0 folder.
        var (asset6, asset8) = (Library("net6.0"), Library("net8.0"));
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net6.0", PACKAGE, asset6);
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, asset8);
        var (project6, project8) = (Project("Project6", 6, "1.0.0", "net6.0", asset6), Project("Project8", 8, "1.0.0", "net8.0", asset8));
        var (touch6, touch8) = (Member(project6, "Api", "Touch"), Member(project8, "Api", "Touch"));
        var (key6, key8) = (TargetOf(project6, touch6).Key, TargetOf(project8, touch8).Key);
        Assert.NotEqual(key6.RelativePath, key8.RelativePath);
        var (file6, file8) = (Stored(key6, GeneratedModelAnswer.OfModel(Model(touch6, LibraryEffect.DeepReadOf("value")))),
                              Stored(key8, GeneratedModelAnswer.OfModel(Model(touch8))));

        foreach (var order in new[] { new[] { project6, project8 }, [project8, project6] })
        {
            var table = new GeneratedModelTable();
            foreach (var project in order)
                table.MapType(project, Member(project, "Api", "Touch").ContainingType, project == project6 ? key6 : key8);
            Assert.Empty(table.AddFile(file8).Concat(table.AddFile(file6)));
            var models = LibraryModels.BuiltIn.WithGenerated(table);

            Assert.Equal([LibraryEffect.DeepReadOf("value")], models.Find(touch6, project6)!.Effects);
            Assert.Empty(models.Find(touch8, project8)!.Effects);
        }
    }

    [Fact]
    public void Entry_applies_only_in_the_compilation_that_passes_it_in_either_order()
    {
        _install.SharedFramework("8.0.5");
        // One package asset, so one file; two dependencies of one name and version in which the asset's Item has, and has not, a CompareTo.
        var (comparable, plain) = (Emit("Dep", "namespace Dep { public sealed class Item : System.IComparable<Item> { public int CompareTo(Item other) => 0; } }"),
                                   Emit("Dep", "namespace Dep { public sealed class Item { } }"));
        var asset = Emit(PACKAGE, ORDERING_LIBRARY, MetadataReference.CreateFromImage(comparable));
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, asset);
        var (first, second) = (Project("First", 8, "1.0.0", "net8.0", asset, dependency: comparable), Project("Second", 8, "1.0.0", "net8.0", asset, dependency: plain));
        var (orderFirst, orderSecond) = (Member(first, "Api", "Order"), Member(second, "Api", "Order"));
        var key = TargetOf(first, orderFirst).Key;
        Assert.Equal(key, TargetOf(second, orderSecond).Key);
        // The model has the library compare the two items: a modelled call to a CompareTo that only the first compilation's Item has.
        var order = new LibraryModel(orderFirst.GetDocumentationCommentId()!, [VersionRange(orderFirst)], [])
        {
            Calls = [new LibraryMemberCall("CompareTo", new ArgumentValue("left"), [[new ArgumentValue("right")]])]
        };
        var file = Stored(key, GeneratedModelAnswer.OfModel(order));
        var before = Hash(file);

        foreach (var projects in new[] { new[] { first, second }, [second, first] })
        {
            var table = new GeneratedModelTable();
            foreach (var project in projects)
                table.MapType(project, Member(project, "Api", "Order").ContainingType, key);
            var diagnostics = table.AddFile(file);
            var models = LibraryModels.BuiltIn.WithGenerated(table);

            var applied = models.Find(orderFirst, first);
            Assert.Equal((LibraryMatchKind.Known, ModelLayer.Generated), (applied!.Kind, applied.Layer));
            Assert.Equal(order.Calls.Select(call => call.Canonical), applied.Calls.Select(call => call.Canonical));
            // The second compilation's Item has no CompareTo: the entry stays in the file, describes no call there and is told so.
            Assert.Null(models.Find(orderSecond, second));
            Assert.Equal(StoredGeneratedKind.ModelNotApplicable, table.Lookup(second, orderSecond, DocumentationCommentId.CreateDeclarationId(orderSecond)!)!.Value.Kind);
            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("does not apply to compilation Second", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("compilation First", diagnostic, StringComparison.Ordinal);
            Assert.Contains("CompareTo", diagnostic, StringComparison.Ordinal);
            Assert.Contains(order.Id, diagnostic, StringComparison.Ordinal);
            Assert.Equal(before, Hash(GeneratedModelFiles.Read(Repository, key)));
        }
    }

    [Fact]
    public void Stores_apply_only_in_the_compilation_where_the_item_converts_in_either_order()
    {
        _install.SharedFramework("8.0.5");
        // One package asset, so one file; two dependencies of one name and version in which the asset's Item is, and is not, a Base.
        var (inheriting, plain) = (Emit("Dep", "namespace Dep { public class Base { } public class Item : Base { } }"),
                                   Emit("Dep", "namespace Dep { public class Base { } public class Item { } }"));
        var asset = Emit(PACKAGE, DEPENDENT_LIBRARY, MetadataReference.CreateFromImage(inheriting));
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, asset);
        var (first, second) = (Project("First", 8, "1.0.0", "net8.0", asset, dependency: inheriting), Project("Second", 8, "1.0.0", "net8.0", asset, dependency: plain));
        var (fillFirst, fillSecond) = (Member(first, "Api", "Fill"), Member(second, "Api", "Fill"));
        var key = TargetOf(first, fillFirst).Key;
        Assert.Equal(key, TargetOf(second, fillSecond).Key);
        // The model stores the item in the bag's cells, which converts only where Item derives from Base.
        var fill = new LibraryModel(fillFirst.GetDocumentationCommentId()!, [VersionRange(fillFirst)], [LibraryEffect.WriteCellsOf("bag")])
        {
            Stores = new Dictionary<string, IReadOnlyList<LibraryValue>> { ["bag"] = [new ArgumentValue("item")] }
        };
        var file = Stored(key, GeneratedModelAnswer.OfModel(fill));
        var before = Hash(file);

        foreach (var order in new[] { new[] { first, second }, [second, first] })
        {
            var table = new GeneratedModelTable();
            foreach (var project in order)
                table.MapType(project, Member(project, "Api", "Fill").ContainingType, key);
            var diagnostics = table.AddFile(file);
            var models = LibraryModels.BuiltIn.WithGenerated(table);

            var applied = models.Find(fillFirst, first);
            Assert.Equal((LibraryMatchKind.Known, ModelLayer.Generated), (applied!.Kind, applied.Layer));
            Assert.Null(models.Find(fillSecond, second));
            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("does not apply to compilation Second", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("compilation First", diagnostic, StringComparison.Ordinal);
            Assert.Contains(fill.Id, diagnostic, StringComparison.Ordinal);
            Assert.Equal(before, Hash(GeneratedModelFiles.Read(Repository, key)));
        }
    }

    [Fact]
    public void Entries_of_a_non_void_method_and_a_getter_are_found_by_their_calls()
    {
        _install.SharedFramework("8.0.5");
        var image = Library("accessors");
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, image);
        var project = Project("App", 8, "1.0.0", "net8.0", image, "using Acme; public static class App { public static int F(int value) => value.Twice(); }");
        var api = project.GetTypeByMetadataName("Acme.Api")!;
        var (compute, getter) = (api.GetMembers("Compute").OfType<IMethodSymbol>().Single(), api.GetMembers("get_Name").OfType<IMethodSymbol>().Single());
        // `value.Twice()` is the extension method reduced to an instance call: its call names the reduced symbol.
        var tree = project.SyntaxTrees.Single();
        var reduced = (IMethodSymbol)project.GetSemanticModel(tree).GetSymbolInfo(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single()).Symbol!;
        Assert.NotNull(reduced.ReducedFrom);
        var twice = reduced.ReducedFrom!;
        var key = TargetOf(project, compute).Key;
        var answers = new[] { compute, getter, twice }.Select(member => GeneratedModelAnswer.OfModel(Model(member))).ToArray();
        // As the generator names them: the id without the `~` and the return type a call is looked up by.
        Assert.Equal(["M:Acme.Api.Compute(System.Int32)", "M:Acme.Api.get_Name", "M:Acme.Ext.Twice(System.Int32)"], answers.Select(answer => answer.Member));
        var file = Stored(key, answers);
        var table = new GeneratedModelTable();
        foreach (var type in new[] { compute.ContainingType, twice.ContainingType })
            table.MapType(project, type, key);
        Assert.Empty(table.AddFile(file));
        var models = LibraryModels.BuiltIn.WithGenerated(table);

        foreach (var (call, lookup) in new[] { (compute, "M:Acme.Api.Compute(System.Int32)~System.Int32"), (getter, "M:Acme.Api.get_Name~System.String"),
                                               (reduced, "M:Acme.Ext.Twice(System.Int32)~System.Int32") })
        {
            var match = models.Find(call, project);
            Assert.NotNull(match);
            Assert.Equal((LibraryMatchKind.Known, ModelLayer.Generated, lookup), (match.Kind, match.Layer, match.MemberId));
        }
    }

    [Fact]
    public void Entry_named_by_another_spelling_of_a_members_id_belongs_to_no_target()
    {
        _install.SharedFramework("8.0.5");
        var image = EmittedAssemblies.Image(PACKAGE, ReferenceScopes.VERSION_ATTRIBUTE + ALIASED_LIBRARY);
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, image);
        var project = Project("App", 8, "1.0.0", "net8.0", image);
        var api = project.GetTypeByMetadataName("Acme.Api")!;
        var plain = api.GetMembers("F").OfType<IMethodSymbol>().Single(method => method.Parameters.Length == 0);
        var compute = api.GetMembers("Compute").OfType<IMethodSymbol>().Single();
        var key = TargetOf(project, plain).Key;
        var (plainId, plainLookup) = (plain.GetDocumentationCommentId()!, DocumentationCommentId.CreateDeclarationId(plain)!);
        var (computeId, computeLookup) = (compute.GetDocumentationCommentId()!, DocumentationCommentId.CreateDeclarationId(compute)!);
        Assert.Equal(("M:Acme.Api.F", "M:Acme.Api.Compute(System.Int32)", "M:Acme.Api.Compute(System.Int32)~System.Int32"), (plainId, computeId, computeLookup));

        // Two spellings Roslyn resolves to a member that are not the id the generator writes for it: `M:Acme.Api.F()` is the id of the
        // function-pointer overload, which resolves to the parameterless F, and the lookup form of Compute carries a return type. A refusal
        // under the first and a model under the second are valid entries of the file; the entries under the members' own ids say otherwise.
        const string POINTER_ID = "M:Acme.Api.F()";
        var spelled = new GeneratedRefusal(ModelReasons.UNKNOWN_TOUCH, "in words", ["probe-handed-to-unseen"]);
        var own = new GeneratedRefusal(GenerationReasons.DRIVER_NOT_SYNTHESIZED, "in words", []);
        var file = Stored(key, GeneratedModelAnswer.OfRefusal(POINTER_ID, spelled),
                          GeneratedModelAnswer.OfModel(new LibraryModel(computeLookup, [VersionRange(compute)], [])),
                          GeneratedModelAnswer.OfRefusal(plainId, own), GeneratedModelAnswer.OfModel(Model(compute)));
        GeneratedModelFile Only(params string[] members) =>
            file with { Entries = file.Entries.Where(entry => members.Contains(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal) };

        // Alone, the spellings install nothing for either member and say nothing: they stay in the file and the members have no answer.
        var spellings = new GeneratedModelTable();
        spellings.MapType(project, api, key);
        Assert.Empty(spellings.AddFile(Only(POINTER_ID, computeLookup)));
        Assert.Equal(StoredGeneratedKind.Absent, spellings.Lookup(project, plain, plainLookup)!.Value.Kind);
        Assert.Equal(StoredGeneratedKind.Absent, spellings.Lookup(project, compute, computeLookup)!.Value.Kind);
        Assert.Null(LibraryModels.BuiltIn.WithGenerated(spellings).Find(compute, project));

        // Beside the entries under the members' own ids they change nothing: each member has the answer written under its own id.
        var table = new GeneratedModelTable();
        table.MapType(project, api, key);
        Assert.Empty(table.AddFile(file));
        Assert.Equal(own.Reason, table.Lookup(project, plain, plainLookup)!.Value.Refusal!.Reason);
        Assert.Equal(StoredGeneratedKind.Model, table.Lookup(project, compute, computeLookup)!.Value.Kind);
        Assert.Equal(ModelLayer.Generated, LibraryModels.BuiltIn.WithGenerated(table).Find(compute, project)!.Layer);
    }

    [Fact]
    public void Project_entry_over_a_generated_model_keeps_the_member_opaque()
    {
        _install.SharedFramework("8.0.5");
        var image = Library("project");
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, image);
        var project = Project("App", 8, "1.0.0", "net8.0", image);
        var touch = Member(project, "Api", "Touch");
        var key = TargetOf(project, touch).Key;
        var table = new GeneratedModelTable();
        table.MapType(project, touch.ContainingType, key);
        Assert.Empty(table.AddFile(Stored(key, GeneratedModelAnswer.OfModel(Model(touch)))));
        var declaredOpaque = new LibraryModel(DocumentationCommentId.CreateDeclarationId(touch)!,
                                              [new SupportedAssemblyVersion(PACKAGE, new Version(0, 0, 0, 0), new Version(9, 0, 0, 0))], [],
                                              ModelLayer.Project, true, touch.ContainingAssembly.Identity.Version);

        var withProject = LibraryModels.WithProject([declaredOpaque]).WithGenerated(table);
        var withoutProject = LibraryModels.BuiltIn.WithGenerated(table);

        // The project entry makes the member opaque, and the layers behind it are not asked.
        var opaque = withProject.Find(touch, project);
        Assert.Equal((LibraryMatchKind.Opaque, ModelLayer.Project, true), (opaque!.Kind, opaque.Layer, opaque.DeclaredOpaque));
        Assert.Equal(ModelLayer.Generated, withoutProject.Find(touch, project)!.Layer);
    }

    [Theory]
    [InlineData("known", "known", "model", "available", "project", false)]
    [InlineData("opaque", "none", "absent", "available", "project", false)]
    [InlineData("opaque", "none", "model", "available", "project", false)]
    [InlineData("none", "known", "model", "available", "builtIn", false)]
    [InlineData("none", "known", "absent", "available", "builtIn", false)]
    [InlineData("none", "outOfRange", "model", "available", "generated", false)]
    [InlineData("none", "outOfRange", "absent", "available", "none", true)]
    [InlineData("none", "none", "model", "available", "generated", false)]
    [InlineData("none", "none", "model", "implementationMissing", "generated", false)]
    [InlineData("none", "none", "absent", "available", "none", true)]
    [InlineData("none", "none", "absent", "implementationMissing", "none", false)]
    [InlineData("none", "none", "absent", "notATarget", "none", false)]
    [InlineData("none", "none", "model", "notATarget", "none", false)]
    [InlineData("none", "none", "refusal", "available", "none", false)]
    [InlineData("none", "none", "modelNotApplicable", "available", "none", false)]
    public void Decide_orders_the_layers_and_generates_only_what_no_answer_covers(string project, string builtIn, string stored, string origin,
                                                                                  string layer, bool generate)
    {
        var decision = GeneratedLayer.Decide(MatchOf(project), MatchOf(builtIn), StoredOf(stored), Enum.Parse<GenerationOrigin>(origin, ignoreCase: true));

        Assert.Equal((Enum.Parse<DescribingLayer>(layer, ignoreCase: true), generate), (decision.Layer, decision.Generate));
    }

    [Fact]
    public void Origin_follows_what_the_target_resolution_answered()
    {
        var key = GeneratedFileKey.ForFramework("System.Linq", new Version(8, 0, 0, 0), "Microsoft.NETCore.App", 8);
        var request = new GenerationRequest("System.Linq", "8.0", "M:System.Linq.Enumerable.Count``1(System.Collections.Generic.IEnumerable{``0})", null, null);

        Assert.Equal(GenerationOrigin.Available, GeneratedLayer.OriginOf(new GenerationTarget(request.MemberId, request.MemberId, key, request)));
        Assert.Equal(GenerationOrigin.ImplementationMissing, GeneratedLayer.OriginOf(new UnavailableGenerationTarget(request.MemberId, request.MemberId, key, "not installed")));
        Assert.All(Enum.GetValues<NoTargetReason>(), reason => Assert.Equal(GenerationOrigin.NotATarget, GeneratedLayer.OriginOf(new NoGenerationTarget(reason))));
    }

    private static LibraryMatch? MatchOf(string kind) => kind switch
    {
        "none" => null,
        "known" => Match(LibraryMatchKind.Known),
        "opaque" => Match(LibraryMatchKind.Opaque),
        "outOfRange" => Match(LibraryMatchKind.OutOfRange),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a match kind of the table.")
    };

    private static LibraryMatch Match(LibraryMatchKind kind) =>
        new(kind, "M:A.B.C", [], new AssemblyIdentity("A"), SupportedAssemblyVersion.Framework("A"));

    private static StoredGeneratedAnswer StoredOf(string kind) => kind switch
    {
        "absent" => StoredGeneratedAnswer.Absent,
        "model" => new StoredGeneratedAnswer(StoredGeneratedKind.Model, new LibraryModel("M:A.B.C", [SupportedAssemblyVersion.Framework("A")], [])),
        "modelNotApplicable" => new StoredGeneratedAnswer(StoredGeneratedKind.ModelNotApplicable),
        "refusal" => new StoredGeneratedAnswer(StoredGeneratedKind.Refusal),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a stored answer of the table.")
    };

    /// <summary>The package's library, told apart from another asset by a comment: a different source is a different MVID.</summary>
    /// <param name="comment">What tells it apart.</param>
    private static byte[] Library(string comment) =>
        EmittedAssemblies.Image(PACKAGE, ReferenceScopes.VERSION_ATTRIBUTE + LIBRARY + "\n// " + comment);

    /// <summary>An assembly of version <c>1.0.0.0</c> compiled against the running runtime and the given references.</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="source">Its source.</param>
    /// <param name="references">What it references besides the runtime.</param>
    private static byte[] Emit(string assemblyName, string source, params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(ReferenceScopes.VERSION_ATTRIBUTE + source)],
                                                   [.. EmittedAssemblies.RuntimeReferences, .. references],
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return stream.ToArray();
    }

    /// <summary>A project of a scope that references the package's asset under a framework's folder, as a project of that framework does.</summary>
    /// <param name="name">The project's assembly name.</param>
    /// <param name="packMajor">The major of the project's <c>Microsoft.NETCore.App.Ref</c>.</param>
    /// <param name="version">The package version.</param>
    /// <param name="framework">The framework folder of the asset the project references.</param>
    /// <param name="image">The asset the project references.</param>
    /// <param name="source">The project's source.</param>
    /// <param name="dependency">A dependency of the package the project references, or <c>null</c> for none.</param>
    private CSharpCompilation Project(string name, int packMajor, string version, string framework, byte[] image,
                                      string source = "public static class App { }", byte[]? dependency = null)
    {
        var references = ReferenceScopes.Runtime(_install, packMajor)
                                        .Append(ReferenceScopes.Reference(image, ReferenceScopes.PackagePath(_install, PACKAGE, version, "lib", framework, PACKAGE)));
        if (dependency is not null)
            references = references.Append(ReferenceScopes.Reference(dependency, ReferenceScopes.Join(_install.Packages, "dep", "1.0.0", "lib", "net8.0", "Dep.dll")));
        return CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source, path: Path.Combine(_install.Root, name + ".cs"))], references,
                                        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private static IMethodSymbol Member(Compilation compilation, string type, string name) =>
        compilation.GetTypeByMetadataName("Acme." + type)!.GetMembers(name).OfType<IMethodSymbol>().Single();

    private GenerationTarget TargetOf(Compilation compilation, IMethodSymbol member) =>
        Assert.IsType<GenerationTarget>(GenerationTargets.Of(member, compilation, _install.Resolver(), CancellationToken.None));

    /// <summary>A model as the generator writes it for a member: its original definition's id, its assembly at its version.</summary>
    /// <param name="member">The member.</param>
    /// <param name="effects">The effects.</param>
    private static LibraryModel Model(IMethodSymbol member, params LibraryEffect[] effects) =>
        new(member.GetDocumentationCommentId()!, [VersionRange(member)], effects);

    private static SupportedAssemblyVersion VersionRange(IMethodSymbol member)
    {
        var version = member.ContainingAssembly.Identity.Version;
        return new SupportedAssemblyVersion(member.ContainingAssembly.Name, version, new Version(version.Major, version.Minor, version.Build, version.Revision + 1));
    }

    /// <summary>Writes answers to the file of a key in the test's repository and reads the file back, as a later run would.</summary>
    /// <param name="key">The file's key.</param>
    /// <param name="answers">The answers.</param>
    private GeneratedModelFile Stored(GeneratedFileKey key, params GeneratedModelAnswer[] answers)
    {
        Assert.Empty(GeneratedModelFiles.Write(GeneratedModelFiles.Read(Repository, key), answers, CancellationToken.None));
        var file = GeneratedModelFiles.Read(Repository, key);
        Assert.False(file.IsUnreadable);
        Assert.Empty(file.Diagnostics);
        Assert.Equal(answers.Length, file.Entries.Count);
        return file;
    }

    private static string Hash(GeneratedModelFile file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullPath!)));

    private static ScopeRun Pipeline(Compilation compilation, LibraryModels models) =>
        ScopePipeline.Run("scope:generated-layer", [compilation], [], Path.GetTempPath(), new ProviderRegistry([new DriverRootProvider()]), models,
                          AnalysisLimits.Default, new(), null, CancellationToken.None);
}
