using System.Text;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>R2: applicable receiver keys, layer precedence and specificity, including generic definitions and incomparable interfaces.</summary>
public sealed class ReceiverRecordSelectionMatrixTests
{
    private static readonly string[] Receivers = ["D", "K", "K2", "S", "G<int>", "G<string>", "G2<int>", "none"];
    private static readonly Lazy<Compilation> Fixture = new(Compile);
    private enum Records
    {
        None, Project, ProjectK, ProjectKAndDefault, ProjectKAndK2, ProjectKAndS, BuiltKProjectDefault,
        ProjectKBuiltDefault, BuiltKGenerated, OpaqueKBuiltDefault,
        ProjectG, ProjectGAndG2, BuiltGProjectDefault,
        ProjectJ1AndJ2, ProjectJ1J2AndC, BuiltJ1AndJ2, BuiltJ1ProjectJ2, ProjectJ1, ProjectJAndJ1
    }

    public static IEnumerable<object[]> Cells()
    {
        foreach (var records in Enum.GetValues<Records>())
        {
            var receivers = records >= Records.ProjectJ1AndJ2 ? new[] { "J", "J1", "C" } :
                records >= Records.ProjectG ? Receivers.Where(receiver => receiver.StartsWith("G", StringComparison.Ordinal)) : Receivers;
            foreach (var receiver in receivers)
                yield return [records.ToString(), receiver];
        }
    }

    [Fact]
    public void There_are_107_cells_and_every_axis_value_occurs()
    {
        var cells = Cells().Select(cell => ((string)cell[0], (string)cell[1])).ToArray();
        Assert.Equal(107, cells.Length);
        Assert.Equal(107, cells.Distinct().Count());
        Assert.All(Enum.GetNames<Records>(), records => Assert.Contains(cells, cell => cell.Item1 == records));
        Assert.All(Receivers.Concat(["J", "J1", "C"]), receiver => Assert.Contains(cells, cell => cell.Item2 == receiver));
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_gets_its_exact_record(string recordsName, string receiver)
    {
        var records = Enum.Parse<Records>(recordsName);
        var method = Method(records >= Records.ProjectJ1AndJ2 ? "J" : "D");
        var models = Models(records, method);
        var match = models.Find(method, Fixture.Value, Type(receiver));

        Assert.Equal(Expected(records, receiver), Describe(match));
        if (match is { Kind: LibraryMatchKind.Opaque })
        {
            Assert.True(match.DeclaredOpaque);
            Assert.Empty(match.Effects);
            Assert.Empty(match.Fates);
            Assert.Empty(match.Outputs);
            Assert.Null(match.Result);
        }
    }

    // Each branch names the R2 rule it asserts; no broad or merely non-narrowing cell is admitted.
    private static string Expected(Records records, string receiver)
    {
        var k = receiver is "K" or "K2";
        var g = receiver is "G<int>" or "G<string>" or "G2<int>";
        return records switch
        {
            Records.None => "none", // R2: no applicable record leaves the member unchanged.
            Records.Project => "Project:default:Known", // R2: a keyless record admits every receiver, including no receiver.
            Records.ProjectK => k ? "Project:K:Known" : "none", // R2: a key admits itself and descendants only.
            Records.ProjectKAndDefault => k ? "Project:K:Known" : "Project:default:Known", // R2: a key beats keyless in its layer.
            Records.ProjectKAndK2 => receiver == "K2" ? "Project:K2:Known" : k ? "Project:K:Known" : "none", // R2: most specific key.
            Records.ProjectKAndS => k ? "Project:K:Known" : receiver == "S" ? "Project:S:Known" : "none", // R2: different keys do not disagree.
            Records.BuiltKProjectDefault => "Project:default:Known", // R2: layer before specificity.
            Records.ProjectKBuiltDefault => k ? "Project:K:Known" : "BuiltIn:default:Known", // R2: layers among applicable records only.
            Records.BuiltKGenerated => k ? "BuiltIn:K:Known" : "Generated:default:Known", // R2: generated only for uncovered calls.
            Records.OpaqueKBuiltDefault => k ? "Project:K:Opaque" : "BuiltIn:default:Known", // R2: applicable opaque is an answer.
            Records.ProjectG => g ? "Project:G`1:Known" : "none", // R2: a definition covers both constructions and descendants.
            Records.ProjectGAndG2 => receiver == "G2<int>" ? "Project:G2`1:Known" : "Project:G`1:Known", // R2: definition specificity.
            Records.BuiltGProjectDefault => "Project:default:Known", // R2: layer before generic specificity.
            Records.ProjectJ1AndJ2 => receiver == "C" ? "Project:default:Opaque" : receiver == "J1" ? "Project:J1:Known" : "none", // R2: incomparable keys.
            Records.ProjectJ1J2AndC => receiver == "C" ? "Project:C:Known" : receiver == "J1" ? "Project:J1:Known" : "none", // R2: C dominates both interfaces.
            Records.BuiltJ1AndJ2 => receiver == "C" ? "BuiltIn:default:Opaque" : receiver == "J1" ? "BuiltIn:J1:Known" : "Generated:default:Known", // R2: no generated fallback on disagreement.
            Records.BuiltJ1ProjectJ2 => receiver == "C" ? "Project:J2:Known" : receiver == "J1" ? "BuiltIn:J1:Known" : "none", // R2: layer before interface specificity.
            Records.ProjectJ1 => receiver is "J1" or "C" ? "Project:J1:Known" : "none", // R2: interface implementation admits the key.
            Records.ProjectJAndJ1 => receiver == "J" ? "Project:J:Known" : "Project:J1:Known", // R2: derived interface dominates its base.
            _ => throw new ArgumentOutOfRangeException(nameof(records))
        };
    }

    private static LibraryModels Models(Records records, IMethodSymbol method)
    {
        LibraryModel P(string? key = null, bool opaque = false) => Record(method, ModelLayer.Project, key, opaque);
        LibraryModel B(string? key = null) => Record(method, ModelLayer.BuiltIn, key);
        LibraryModel[] entries = records switch
        {
            Records.None => [],
            Records.Project => [P()],
            Records.ProjectK => [P("K")],
            Records.ProjectKAndDefault => [P("K"), P()],
            Records.ProjectKAndK2 => [P("K"), P("K2")],
            Records.ProjectKAndS => [P("K"), P("S")],
            Records.BuiltKProjectDefault => [B("K"), P()],
            Records.ProjectKBuiltDefault => [P("K"), B()],
            Records.BuiltKGenerated => [B("K")],
            Records.OpaqueKBuiltDefault => [P("K", opaque: true), B()],
            Records.ProjectG => [P("G`1")],
            Records.ProjectGAndG2 => [P("G`1"), P("G2`1")],
            Records.BuiltGProjectDefault => [B("G`1"), P()],
            Records.ProjectJ1AndJ2 => [P("J1"), P("J2")],
            Records.ProjectJ1J2AndC => [P("J1"), P("J2"), P("C")],
            Records.BuiltJ1AndJ2 => [B("J1"), B("J2")],
            Records.BuiltJ1ProjectJ2 => [B("J1"), P("J2")],
            Records.ProjectJ1 => [P("J1")],
            Records.ProjectJAndJ1 => [P("J"), P("J1")],
            _ => throw new ArgumentOutOfRangeException(nameof(records))
        };
        var models = new LibraryModels(new LibraryModels(entries.Where(entry => entry.Layer == ModelLayer.BuiltIn), []),
                                       entries.Where(entry => entry.Layer == ModelLayer.Project));
        if (records is not (Records.BuiltKGenerated or Records.BuiltJ1AndJ2))
            return models;
        var compilation = Fixture.Value;
        var id = DocumentationCommentId.CreateDeclarationId(method)!;
        var key = GeneratedFileKey.ForPackage("Receivers", new Version(1, 0, 0, 0), "Receivers", "1.0.0", Guid.Empty);
        var table = new GeneratedModelTable();
        table.MapType(compilation, method.ContainingType, key);
        var generated = new ProjectModelEntry("generated.json", 0, id, ["Receivers"], null, [], false);
        Assert.Empty(table.AddFile(new GeneratedModelFile(key, "generated.json", null,
            new Dictionary<string, GeneratedModelEntry> { [id] = new("generated.json", id, generated, null) }, [])));
        return models.WithGenerated(table);
    }

    [Fact]
    public void Built_in_rebuild_keeps_all_keys_and_repeated_pairs_still_fail()
    {
        var method = Method("D");
        var keyed = Record(method, ModelLayer.BuiltIn, "K");
        var keyless = Record(method, ModelLayer.BuiltIn, null);
        var original = new LibraryModels([keyed, keyless], []);
        var rebuilt = new LibraryModels(original.Members, original.ImmutableTypes);
        Assert.Equal(2, rebuilt.Members.Count);
        Assert.Equal("BuiltIn:K:Known", Describe(rebuilt.Find(method, Fixture.Value, Type("K"))));
        Assert.Throws<LibraryModelException>(() => new LibraryModels([keyed, keyed], []));
        Assert.Throws<LibraryModelException>(() => new LibraryModels([keyless, keyless], []));
    }

    [Fact]
    public void Resolver_groups_conflicts_by_key_and_keeps_the_other_keys()
    {
        var method = Method("D");
        var records = new[] { Record(method, ModelLayer.Project, null), Record(method, ModelLayer.Project, "K"),
                              Record(method, ModelLayer.Project, "K", opaque: true), Record(method, ModelLayer.Project, "S") };
        var bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"models\":[" +
            string.Join(',', records.Select(EntryText)) + "]}");
        var files = ProjectModelFiles.Read("matrix.json", bytes);
        var result = ProjectModelResolver.Resolve(files, [Fixture.Value], ModelLock.Read(Engine.EngineFixture.ROOT_DIRECTORY, files));
        Assert.Equal(2, result.Rejections.Count);
        Assert.Equal("Project:default:Known", Describe(result.Models.Find(method, Fixture.Value, Type("D"))));
        Assert.Equal("Project:K:Opaque", Describe(result.Models.Find(method, Fixture.Value, Type("K"))));
        Assert.Equal("Project:S:Known", Describe(result.Models.Find(method, Fixture.Value, Type("S"))));
    }

    [Fact]
    public void Assembly_and_version_filters_precede_receiver_specificity()
    {
        var method = Method("D");
        var keyless = Record(method, ModelLayer.BuiltIn, null);
        var keyed = Record(method, ModelLayer.BuiltIn, "K");
        var foreign = keyed with { Assemblies = [new("Other", new Version(0, 0), new Version(9, 0))] };
        var outOfRange = keyed with { Assemblies = [new("Receivers", new Version(2, 0), new Version(3, 0))] };
        Assert.Equal("BuiltIn:default:Known", Describe(new LibraryModels([foreign, keyless], []).Find(method, Fixture.Value, Type("K"))));
        Assert.Equal("BuiltIn:default:Known", Describe(new LibraryModels([outOfRange, keyless], []).Find(method, Fixture.Value, Type("K"))));
        var project = Record(method, ModelLayer.Project, "K") with { ResolvedVersion = new Version(2, 0, 0, 0) };
        Assert.Equal("BuiltIn:default:Known", Describe(new LibraryModels(new LibraryModels([keyless], []), [project]).Find(method, Fixture.Value, Type("K"))));
    }

    [Fact]
    public void Inapplicable_member_key_keeps_the_immutable_type_rule()
    {
        var method = Fixture.Value.GetTypeByMetadataName("Receivers.D")!.GetMembers("Pure").OfType<IMethodSymbol>().Single();
        var keyed = Record(method, ModelLayer.BuiltIn, "K", opaque: true);
        var immutable = new ImmutableLibraryType("T:Receivers.D", keyed.Assemblies);
        var models = new LibraryModels([keyed], [immutable]);
        Assert.Equal("BuiltIn:default:Known", Describe(models.Find(method, Fixture.Value, Type("D"))));
        Assert.Equal("BuiltIn:default:Known", Describe(models.Find(method, Fixture.Value)));
        Assert.Equal("BuiltIn:K:Opaque", Describe(models.Find(method, Fixture.Value, Type("K"))));
    }

    private static string Describe(LibraryMatch? match) => match is null ? "none" :
        $"{match.Layer}:{match.Receiver?.Replace("T:Receivers.", "", StringComparison.Ordinal) ?? "default"}:{match.Kind}";

    private static string EntryText(LibraryModel record)
    {
        var entry = ModelEntryWriter.Write(record);
        if (record.DeclaredOpaque)
        {
            entry.Remove("effects");
            entry["opaque"] = true;
        }
        return entry.ToJsonString();
    }

    private static LibraryModel Record(IMethodSymbol method, ModelLayer layer, string? receiver, bool opaque = false) =>
        new(DocumentationCommentId.CreateDeclarationId(method)!, [new("Receivers", new Version(1, 0, 0, 0), new Version(2, 0, 0, 0))], [],
            layer, opaque, layer == ModelLayer.Project ? new Version(1, 0, 0, 0) : null, receiver is null ? null : "T:Receivers." + receiver);

    private static IMethodSymbol Method(string type) => Fixture.Value.GetTypeByMetadataName("Receivers." + type)!
        .GetMembers(type == "J" ? "N" : "M").OfType<IMethodSymbol>().Single();

    private static ITypeSymbol? Type(string name) => name switch
    {
        "none" => null,
        "G<int>" => Fixture.Value.GetTypeByMetadataName("Receivers.G`1")!.Construct(Fixture.Value.GetSpecialType(SpecialType.System_Int32)),
        "G<string>" => Fixture.Value.GetTypeByMetadataName("Receivers.G`1")!.Construct(Fixture.Value.GetSpecialType(SpecialType.System_String)),
        "G2<int>" => Fixture.Value.GetTypeByMetadataName("Receivers.G2`1")!.Construct(Fixture.Value.GetSpecialType(SpecialType.System_Int32)),
        _ => Fixture.Value.GetTypeByMetadataName("Receivers." + name)!
    };

    private static Compilation Compile()
    {
        var library = CSharpCompilation.Create("Receivers", [CSharpSyntaxTree.ParseText("""
            [assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
            namespace Receivers;
            public class D { public void M(object value) { } public void Pure() { } }
            public class K : D { }
            public class K2 : K { }
            public class S : D { }
            public class G<T> : D { }
            public class G2<T> : G<T> { }
            public interface J { void N(object value); }
            public interface J1 : J { }
            public interface J2 : J { }
            public class C : J1, J2 { public void N(object value) { } }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        var emitted = library.Emit(bytes);
        Assert.True(emitted.Success, string.Join('\n', emitted.Diagnostics));
        return CSharpCompilation.Create("Consumer", [], StubAssemblies.PlatformWithout([]).Append(MetadataReference.CreateFromImage(bytes.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
