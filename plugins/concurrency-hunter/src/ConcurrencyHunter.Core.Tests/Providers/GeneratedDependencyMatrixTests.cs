using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>One lowering read in each generation compilation, with applicability and raw dependency views checked separately.</summary>
public sealed class GeneratedDependencyMatrixTests
{
    private enum BuiltIn { None, InRange, OutOfRange }
    private enum Project { None, Present }
    private enum Implementation { OnDisk, Missing }
    private enum Answer { None, StoredModel, StoredRefusal, RunModel, RunRefusal }
    private enum Applicability { Applies, WrongSignature, Dictionary, LibraryOnly }
    private enum Callee { Dependency, CoreLib, OwnAssembly }
    private enum Context { Library, Driver, RecipeCopy }
    private enum Reading { Call, Out, Ref }

    private sealed record Cell(BuiltIn BuiltIn, Project Project, Implementation Implementation, Answer Answer,
                               Applicability Applicability, Callee Callee, Context Context, Reading Reading);

    private static readonly Cell[] Cells = (from builtIn in Enum.GetValues<BuiltIn>()
                                            from project in Enum.GetValues<Project>()
                                            from implementation in Enum.GetValues<Implementation>()
                                            from answer in Enum.GetValues<Answer>()
                                            from applicability in Enum.GetValues<Applicability>()
                                            from callee in Enum.GetValues<Callee>()
                                            from context in Enum.GetValues<Context>()
                                            from reading in Enum.GetValues<Reading>()
                                            select new Cell(builtIn, project, implementation, answer, applicability, callee, context, reading))
        .Where(Allowed).ToArray();

    private static bool IsModel(Answer answer) => answer is Answer.StoredModel or Answer.RunModel;

    private static bool Allowed(Cell cell)
    {
        // CoreLib and the generated assembly have no dependency file and cannot have a missing implementation here.
        if (cell.Callee != Callee.Dependency && (cell.Answer != Answer.None || cell.Implementation != Implementation.OnDisk))
            return false;
        // A member without an implementation cannot be generated in this run.
        if (cell.Implementation == Implementation.Missing && cell.Answer is Answer.RunModel or Answer.RunRefusal)
            return false;
        // Applicability is an axis only for a model answer.
        if (!IsModel(cell.Answer) && cell.Applicability != Applicability.Applies)
            return false;
        // The CoreLib fixture has an out overload, but no ref overload outside the engine's recognized members.
        return cell.Callee != Callee.CoreLib || cell.Reading != Reading.Ref;
    }

    [Fact]
    public void Every_cell_gets_its_expected_answer()
    {
        using var fixture = new Fixture();
        foreach (var cell in Cells)
        {
            var actual = fixture.Read(cell, withSource: true);
            Assert.True(ExpectedLayer(cell) == actual.Layer, $"{cell}: expected {ExpectedLayer(cell)}, got {actual.Layer}");
            var view = AssertView(cell, actual.Views);
            if (view?.Kind == DependencyViewKind.Model)
            {
                Assert.Equal(GeneratedModelFiles.ModelText(actual.Entry!), view.ModelText);
                Assert.Equal(GeneratedModelFiles.ModelText(fixture.Entry(cell, actual.Member, stored: false)!), view.ModelText);
            }
            if (cell.Reading != Reading.Call && cell.Callee == Callee.Dependency && actual.Layer == ModelLayer.Generated)
                Assert.True(actual.HasOutput, $"{cell}: the out/ref reader lost the generated output");
        }
    }

    [Fact]
    public void No_cell_narrows()
    {
        using var fixture = new Fixture();
        foreach (var cell in Cells)
        {
            var actual = fixture.Read(cell, withSource: true);
            Assert.NotEqual(ModelLayer.Project, actual.Layer);
            if (actual.Layer == ModelLayer.Generated)
            {
                Assert.True(IsModel(cell.Answer), cell.ToString());
                Assert.True(cell.Callee == Callee.Dependency && cell.BuiltIn != BuiltIn.InRange, cell.ToString());
                Assert.True(cell.Applicability == Applicability.Applies ||
                            cell.Applicability == Applicability.LibraryOnly && cell.Context != Context.Driver, cell.ToString());
            }
            if (cell.BuiltIn == BuiltIn.InRange && cell.Callee != Callee.OwnAssembly)
                Assert.Equal(ModelLayer.BuiltIn, actual.Layer);
        }
    }

    [Fact]
    public void Every_axis_value_occurs()
    {
        Assert.Equal(Cells.Length, Cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<BuiltIn>(), Cells.Select(cell => cell.BuiltIn).Distinct().Order());
        Assert.Equal(Enum.GetValues<Project>(), Cells.Select(cell => cell.Project).Distinct().Order());
        Assert.Equal(Enum.GetValues<Implementation>(), Cells.Select(cell => cell.Implementation).Distinct().Order());
        Assert.Equal(Enum.GetValues<Answer>(), Cells.Select(cell => cell.Answer).Distinct().Order());
        Assert.Equal(Enum.GetValues<Applicability>(), Cells.Select(cell => cell.Applicability).Distinct().Order());
        Assert.Equal(Enum.GetValues<Callee>(), Cells.Select(cell => cell.Callee).Distinct().Order());
        Assert.Equal(Enum.GetValues<Context>(), Cells.Select(cell => cell.Context).Distinct().Order());
        Assert.Equal(Enum.GetValues<Reading>(), Cells.Select(cell => cell.Reading).Distinct().Order());
        // B: (1 no answer + 2 models x 4 fits + 2 refusals) on disk, (1 + 4 + 1) missing; CoreLib and A: one answer.
        Assert.Equal(3 * 2 * ((11 + 6) * 3 * 3 + 3 * 2 + 3 * 3), Cells.Length);
    }

    [Fact]
    public void Without_a_dependency_source_every_cell_answers_as_today()
    {
        using var fixture = new Fixture();
        foreach (var cell in Cells)
        {
            var actual = fixture.Read(cell, withSource: false);
            Assert.Equal(cell.Callee != Callee.OwnAssembly && cell.BuiltIn == BuiltIn.InRange ? ModelLayer.BuiltIn : (ModelLayer?)null, actual.Layer);
            Assert.Empty(actual.Views);
        }
    }

    [Fact]
    public void Members_of_one_type_get_their_own_answers_in_either_order()
    {
        using var fixture = new Fixture();
        var compilation = fixture.Compilation(Context.Library, Callee.Dependency, Reading.Call, Implementation.OnDisk);
        var type = compilation.GetTypeByMetadataName("B.Api")!;
        var modelMember = type.GetMembers("Touch").OfType<IMethodSymbol>().Single();
        var refusedMember = type.GetMembers("Other").OfType<IMethodSymbol>().Single();
        var pointerMember = type.GetMembers("Pointer").OfType<IMethodSymbol>().Single();
        var model = Model(modelMember);
        var entry = GeneratedModelFiles.EntryOf(GeneratedModelAnswer.OfModel(model), "run")!;
        var refusal = GeneratedModelFiles.EntryOf(Refusal(refusedMember), "run")!;
        foreach (var members in new[] { new[] { modelMember, refusedMember, pointerMember }, [pointerMember, refusedMember, modelMember] })
        {
            var queried = new List<string>();
            var dependencies = new DependencyModels(fixture.Resolver(Implementation.OnDisk), "A", 8, (member, key) =>
            {
                queried.Add(member);
                return member == entry.Member ? entry : member == refusal.Member ? refusal : throw new InvalidOperationException(member);
            });
            var models = new LibraryModels([], []).WithDependencies(dependencies);
            foreach (var member in members)
            {
                var match = models.Find(member, compilation);
                Assert.Equal(SymbolEqualityComparer.Default.Equals(member, modelMember) ? ModelLayer.Generated : (ModelLayer?)null, match?.Layer);
            }
            Assert.Equal(new[] { modelMember.GetDocumentationCommentId(), refusedMember.GetDocumentationCommentId() }.Order(), queried.Order());
            Assert.Equal(2, dependencies.Views.Count);
            Assert.Equal(DependencyViewKind.Model, dependencies.Views.Single(view => view.Target.MemberId == entry.Member).Kind);
            Assert.Equal(DependencyViewKind.Refusal, dependencies.Views.Single(view => view.Target.MemberId == refusal.Member).Kind);
            Assert.Equal(dependencies.Views.OrderBy(view => view.Target.MemberId, StringComparer.Ordinal), dependencies.Views);
        }
    }

    [Fact]
    public void A_generation_reports_the_view_of_each_dependency_it_called()
    {
        using var install = new GenerationInstall();
        install.SharedFramework("8.0.5", runtime: true);
        var b = EmittedAssemblies.Image("B", ReferenceScopes.VERSION_ATTRIBUTE + """
            namespace B { public static class Api {
                public static void Modelled(object item) { }
                public static void Unanswered(object item) { }
            } }
            """);
        var path = ReferenceScopes.AddAsset(install, "B", "1.0.0", "net8.0", "B", b);
        var a = Emit("A", """
            namespace A { public static class Api {
                public static void Run(object item) { B.Api.Modelled(item); B.Api.Unanswered(item); }
            } }
            """, MetadataReference.CreateFromFile(path));
        ReferenceScopes.AddAsset(install, "A", "1.0.0", "net8.0", "A", a,
            "<dependencies><group targetFramework='net8.0'><dependency id='B' version='[1.0.0]' /></group></dependencies>");
        var request = new GenerationRequest("A", "1.0.0", "M:A.Api.Run(System.Object)", "A", "net8.0");
        GeneratedModelEntry? Source(string member, GeneratedFileKey key) => member.Contains("Modelled", StringComparison.Ordinal)
            ? GeneratedModelFiles.EntryOf(GeneratedModelAnswer.OfModel(new LibraryModel(member,
                [new SupportedAssemblyVersion("B", new Version(1, 0, 0, 0), new Version(1, 0, 0, 1))], [])), "run") : null;

        var answer = ModelGenerator.Generate(request, install.Resolver(), CancellationToken.None, Source);

        Assert.Equal(new[] { "M:B.Api.Modelled(System.Object)", "M:B.Api.Unanswered(System.Object)" }, answer.DependencyViews.Select(view => view.Target.MemberId));
        Assert.Equal(new[] { DependencyViewKind.Model, DependencyViewKind.NoAnswer }, answer.DependencyViews.Select(view => view.Kind));
        Assert.All(answer.DependencyViews, view => Assert.Equal("B", view.Target.Key.Package));
        Assert.Empty(ModelGenerator.Generate(request, install.Resolver(), CancellationToken.None).DependencyViews);
        var wire = System.Text.Encoding.UTF8.GetString(GeneratedDocument.Write(answer));
        Assert.DoesNotContain("DependencyViews", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dependencyViews", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_text_compares_independent_entries_by_the_file_form()
    {
        using var install = new GenerationInstall();
        var model = new LibraryModel("M:B.Api.Copy(System.Object@,System.Object@)",
            [new SupportedAssemblyVersion("B", new Version(1, 0, 0, 0), new Version(2, 0, 0, 0))],
            [LibraryEffect.DeepReadOf("first"), LibraryEffect.DeepReadOf("second")])
        {
            Outputs = new Dictionary<string, LibraryResult>
            {
                ["second"] = new(LibraryResultKind.OneOf, [new ArgumentValue("second")]),
                ["first"] = new(LibraryResultKind.OneOf, [new ArgumentValue("first")])
            }
        };
        var entry = GeneratedModelFiles.EntryOf(GeneratedModelAnswer.OfModel(model), "run")!;
        var reordered = model with
        {
            Effects = model.Effects.Reverse().ToArray(),
            Outputs = model.Outputs.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value)
        };
        var key = GeneratedFileKey.ForFramework("B", new Version(1, 0, 0, 0), "Microsoft.NETCore.App", 8);
        Assert.Empty(GeneratedModelFiles.Write(GeneratedModelFiles.Read(install.Root, key),
            [GeneratedModelAnswer.OfModel(reordered)], CancellationToken.None));
        var stored = GeneratedModelFiles.Read(install.Root, key).Entries[model.Id];

        Assert.NotSame(entry.Model, stored.Model);
        Assert.Equal(GeneratedModelFiles.ModelText(entry), GeneratedModelFiles.ModelText(stored));
        var changed = GeneratedModelFiles.EntryOf(GeneratedModelAnswer.OfModel(model with { Effects = [] }), "run")!;
        Assert.NotEqual(GeneratedModelFiles.ModelText(entry), GeneratedModelFiles.ModelText(changed));
        var refusal = GeneratedModelFiles.EntryOf(GeneratedModelAnswer.OfRefusal(model.Id,
            new GeneratedRefusal(GenerationReasons.NOT_A_CANDIDATE, "fixture refusal", [])), "run")!;
        Assert.Null(GeneratedModelFiles.ModelText(refusal));
    }

    private static ModelLayer? ExpectedLayer(Cell cell) => cell.Callee == Callee.OwnAssembly ? null :
        cell.BuiltIn == BuiltIn.InRange ? ModelLayer.BuiltIn : cell.Callee == Callee.Dependency && IsModel(cell.Answer) &&
        (cell.Applicability == Applicability.Applies || cell.Applicability == Applicability.LibraryOnly && cell.Context != Context.Driver)
            ? ModelLayer.Generated : null;

    private static DependencyView? AssertView(Cell cell, IReadOnlyList<DependencyView> views)
    {
        if (cell.Callee != Callee.Dependency || cell.BuiltIn == BuiltIn.InRange)
        {
            Assert.Empty(views);
            return null;
        }
        var view = Assert.Single(views);
        var expected = IsModel(cell.Answer) ? DependencyViewKind.Model : cell.Answer != Answer.None ? DependencyViewKind.Refusal :
            cell.Implementation == Implementation.Missing ? DependencyViewKind.NoImplementation : DependencyViewKind.NoAnswer;
        Assert.Equal(expected, view.Kind);
        Assert.Equal(cell.Implementation == Implementation.Missing, view.Target is UnavailableGenerationTarget);
        Assert.Equal(IsModel(cell.Answer), view.ModelText is not null);
        return view;
    }

    private static GeneratedModelAnswer Refusal(IMethodSymbol member) => GeneratedModelAnswer.OfRefusal(member.GetDocumentationCommentId()!,
        new GeneratedRefusal(GenerationReasons.NOT_A_CANDIDATE, "fixture refusal", []));

    private static LibraryModel Model(IMethodSymbol member) => new(member.GetDocumentationCommentId()!,
        [new SupportedAssemblyVersion(member.ContainingAssembly.Name, member.ContainingAssembly.Identity.Version,
            new Version(member.ContainingAssembly.Identity.Version.Major + 1, 0, 0, 0))], []);

    private static byte[] Emit(string name, string source, params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(ReferenceScopes.VERSION_ATTRIBUTE + source)],
            [.. EmittedAssemblies.RuntimeReferences, .. references], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join('\n', emitted.Diagnostics));
        return stream.ToArray();
    }

    private sealed record ReadAnswer(ModelLayer? Layer, IReadOnlyList<DependencyView> Views, GeneratedModelEntry? Entry, IMethodSymbol Member, bool HasOutput);

    private sealed class Fixture : IDisposable
    {
        private const string API = """
            public static unsafe class Api {
                public static void Touch(Dep.Item item) { }
                public static void Fill(Dep.Item item, out object output) { output = item; }
                public static void Change(Dep.Item item, ref object output) { output = item; }
                public static void Other(Dep.Item item) { }
                public static void Pointer(delegate*<void> callback) { }
            }
            """;
        private readonly GenerationInstall _available = new();
        private readonly GenerationInstall _missing = new();
        private readonly Dictionary<(Context, Callee, Reading, Implementation), CSharpCompilation> _compilations = new();
        private readonly Dictionary<(Answer, Applicability, Reading, string), GeneratedModelEntry> _entries = new();
        private readonly byte[] _comparable = EmittedAssemblies.Image("Dep", "namespace Dep { public class Item { public int CompareTo(Item other) => 0; } }");
        private readonly byte[] _plain = EmittedAssemblies.Image("Dep", "namespace Dep { public class Item { } }");
        private readonly byte[] _b;

        internal Fixture()
        {
            _b = Emit("B", "namespace B { " + API + " }", MetadataReference.CreateFromImage(_comparable));
            var folder = _available.SharedFramework("8.0.5", runtime: true);
            File.WriteAllBytes(Path.Combine(folder, "B.dll"), _b);
            _missing.SharedFramework("8.0.5", runtime: true);
        }

        public void Dispose() { _available.Dispose(); _missing.Dispose(); }

        internal ImplementationAssemblies Resolver(Implementation implementation) => Install(implementation).Resolver();

        private GenerationInstall Install(Implementation implementation) => implementation == Implementation.OnDisk ? _available : _missing;

        internal CSharpCompilation Compilation(Context context, Callee callee, Reading reading, Implementation implementation)
        {
            var identity = (context, callee, reading, implementation);
            if (_compilations.TryGetValue(identity, out var cached))
                return cached;
            var install = Install(implementation);
            var dependency = ReferenceScopes.Reference(_b, Path.Combine(install.DotnetRoot, "shared", "Microsoft.NETCore.App", "8.0.5", "B.dll"));
            var dep = MetadataReference.CreateFromImage(_comparable);
            var library = CSharpCompilation.Create("A", [CSharpSyntaxTree.ParseText("namespace A { " + API + " public static class Entry { public static object Run(object item) => item; } }")],
                [.. EmittedAssemblies.RuntimeReferences, dependency, dep], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            CSharpCompilation compilation;
            if (context == Context.Driver)
            {
                var member = library.GetTypeByMetadataName("A.Entry")!.GetMembers("Run").Single();
                var synthesis = DriverSynthesizer.Synthesize(library, member, new HashSet<string>(), CancellationToken.None);
                var driver = synthesis.Driver;
                Assert.True(driver is not null, synthesis.Reason + ": " + synthesis.Detail);
                compilation = driver.Compilation.RemoveReferences(dep).AddReferences(MetadataReference.CreateFromImage(_plain));
            }
            else if (context == Context.RecipeCopy)
            {
                var result = new LibraryCompilationResult(library, null, 1, 0, new HashSet<string>(), new HashSet<string>(), []);
                compilation = LibraryCompilation.RecipeCopy(result, new GenerationChecks(CancellationToken.None)).Compilation!;
            }
            else
                compilation = library;
            var call = callee == Callee.CoreLib
                ? reading == Reading.Call ? "System.GC.KeepAlive(item);" : "System.Enum.TryParse(typeof(System.DayOfWeek), \"Monday\", out output);"
                : (callee == Callee.Dependency ? "B" : "A") + ".Api." + (reading switch
                {
                    Reading.Call => "Touch(item);", Reading.Out => "Fill(item, out output);", _ => "Change(item, ref output);"
                });
            compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
                "public static class ReadSite { public static object Run(Dep.Item item) { object output = item; " + call + " return output; } }",
                options: (CSharpParseOptions)compilation.SyntaxTrees.First().Options,
                path: Path.Combine(install.Root, "ReadSite.cs")));
            Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            _compilations.Add(identity, compilation);
            return compilation;
        }

        internal ReadAnswer Read(Cell cell, bool withSource)
        {
            var compilation = Compilation(cell.Context, cell.Callee, cell.Reading, cell.Implementation);
            var member = CalledMember(compilation, cell);
            var entry = Entry(cell, member, stored: cell.Answer is Answer.StoredModel or Answer.StoredRefusal);
            var builtin = Model(member) with { Id = DocumentationCommentId.CreateDeclarationId(member)! };
            if (cell.BuiltIn == BuiltIn.OutOfRange)
                builtin = builtin with { Assemblies = [new SupportedAssemblyVersion(member.ContainingAssembly.Name, new Version(99, 0, 0, 0), new Version(100, 0, 0, 0))] };
            var models = new LibraryModels(cell.BuiltIn == BuiltIn.None ? [] : [builtin], []);
            // Project entries are deliberately on the input set; generation must remove them before any reader consults the set.
            if (cell.Project == Project.Present)
                models = new LibraryModels(models, [builtin with { Layer = ModelLayer.Project, ResolvedVersion = member.ContainingAssembly.Identity.Version }]);
            var dependencies = new DependencyModels(Resolver(cell.Implementation), "A", 8, (id, key) =>
            {
                Assert.Equal(member.GetDocumentationCommentId(), id);
                Assert.Equal("B", key.AssemblyName);
                return entry;
            });
            models = withSource ? models.WithDependencies(dependencies) : new LibraryModels(models.Members.Where(model => model.Layer == ModelLayer.BuiltIn &&
                model.Assemblies.All(range => range.AssemblyName != "A")), models.ImmutableTypes);
            var site = compilation.GetTypeByMetadataName("ReadSite")!.GetMembers("Run").OfType<IMethodSymbol>().Single();
            var lowered = IrLowering.Lower(site, compilation, Path.GetTempPath(), CancellationToken.None, models);
            var match = models.Find(member, compilation);
            var operations = lowered.Body.Blocks.SelectMany(block => block.Operations).ToArray();
            var applied = operations.OfType<IrCallOperation>().Select(call => call.Library).SingleOrDefault(call => call is { InRange: true });
            var layer = applied?.Layer switch
            {
                IrModelLayer.BuiltIn => ModelLayer.BuiltIn, IrModelLayer.Project => ModelLayer.Project,
                IrModelLayer.Generated => ModelLayer.Generated, _ => (ModelLayer?)null
            };
            Assert.Equal(match?.Kind == LibraryMatchKind.Known ? match.Layer : null, layer);
            if (cell.Callee == Callee.Dependency && cell.Reading != Reading.Call && layer == ModelLayer.Generated)
            {
                Assert.DoesNotContain(operations.OfType<IrUnknownOperation>(), operation => operation.Reason == "opaque-out-value");
                var call = Assert.Single(operations.OfType<IrCallOperation>(), call => call.Library is { InRange: true });
                Assert.True(call.RefResults.ContainsKey(1));
            }
            return new ReadAnswer(layer, dependencies.Views, entry, member, applied is { Outputs.Count: > 0 });
        }

        internal GeneratedModelEntry? Entry(Cell cell, IMethodSymbol member, bool stored)
        {
            if (cell.Answer == Answer.None)
                return null;
            var key = (cell.Answer, cell.Applicability, cell.Reading, member.GetDocumentationCommentId()!);
            if (stored && _entries.TryGetValue(key, out var cached))
                return cached;
            GeneratedModelAnswer answer;
            if (!IsModel(cell.Answer))
                answer = Refusal(member);
            else
            {
                var model = Model(member);
                if (cell.Reading != Reading.Call)
                    model = model with { Outputs = new Dictionary<string, LibraryResult> { ["output"] = new(LibraryResultKind.OneOf, [new ArgumentValue("item")]) } };
                model = cell.Applicability switch
                {
                    Applicability.WrongSignature => model with { Effects = [LibraryEffect.DeepReadOf("wrongParameter")] },
                    Applicability.Dictionary => model with { Result = new LibraryResult(LibraryResultKind.Dictionary, [new ArgumentValue("item"), new ArgumentValue("item")]) },
                    Applicability.LibraryOnly => model with { Calls = [new LibraryMemberCall("CompareTo", new ArgumentValue("item"), [[new ArgumentValue("item")]])] },
                    _ => model
                };
                answer = GeneratedModelAnswer.OfModel(model);
            }
            if (!stored)
                return Assert.IsType<GeneratedModelEntry>(GeneratedModelFiles.EntryOf(answer, "run"));
            var repository = Path.Combine(_available.Root, "stored", _entries.Count.ToString());
            var fileKey = GeneratedFileKey.ForFramework("B", new Version(1, 0, 0, 0), "Microsoft.NETCore.App", 8);
            Assert.Empty(GeneratedModelFiles.Write(GeneratedModelFiles.Read(repository, fileKey), [answer], CancellationToken.None));
            var entry = GeneratedModelFiles.Read(repository, fileKey).Entries[answer.Member];
            _entries.Add(key, entry);
            return entry;
        }

        private static IMethodSymbol CalledMember(Compilation compilation, Cell cell)
        {
            if (cell.Callee == Callee.CoreLib)
                return cell.Reading == Reading.Call ? compilation.GetTypeByMetadataName("System.GC")!.GetMembers("KeepAlive").OfType<IMethodSymbol>().Single() :
                    compilation.GetTypeByMetadataName("System.Enum")!.GetMembers("TryParse").OfType<IMethodSymbol>()
                        .Single(method => !method.IsGenericMethod && method.Parameters.Length == 3 && method.Parameters[1].Type.SpecialType == SpecialType.System_String);
            return compilation.GetTypeByMetadataName((cell.Callee == Callee.Dependency ? "B" : "A") + ".Api")!
                .GetMembers(cell.Reading switch { Reading.Call => "Touch", Reading.Out => "Fill", _ => "Change" }).OfType<IMethodSymbol>().Single();
        }
    }
}
