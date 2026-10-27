using System.Text;
using System.Text.Json.Nodes;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The record and member steps of runs and receiver, through both curated readers and the nested generated reader.
/// Every cell is exact; every accepted cell also survives writing and reading as a project record.</summary>
public sealed class RecordRunsReceiverMatrixTests
{
    private static readonly string?[] Runs = [null, "concurrent", "sequential", "sequential-awaited", "parallel"];
    private static readonly string[] Fates = ["invoke-now", "iterator", "holder", "startup", "unknown-execution", "not-run"];
    private static readonly string[] Returns = ["void", "int", "Task", "ValueTask", "Task<int>", "ValueTask<int>"];
    private static readonly string?[] Results = [null, "task([completion(returns:f)])", "[returns:f]", "[arg:t]", "[kept:t]", "[new]", "[arg:t,returns:f]"];
    private static readonly Lazy<Compilation> Fixture = new(Compile);
    private static readonly GeneratedFileKey Key = GeneratedFileKey.ForPackage("Records", new Version(1, 0, 0, 0), "Records", "1.0.0",
        Guid.Parse("785d6d61-a560-480c-80e0-1d43b30e157c"));

    private enum Reader { BuiltIn, Project, Generated }
    private enum Member { Instance, Getter, Interface, Static, Constructor, Extension }
    private enum Receiver { None, Declaring, Derived, Object, Unrelated, ImplementedInterface, ForeignDerived, NoPrefix, Missing }
    private sealed record RunsCell(string? Runs, string Fate, string Return, Reader Reader);
    private sealed record ResultCell(string? Runs, string? Result, Reader Reader);
    private sealed record ReceiverCell(Receiver Receiver, Member Member, Reader Reader);

    private static IEnumerable<RunsCell> RunsCells() => Runs.SelectMany(runs => Fates.SelectMany(fate =>
        Returns.SelectMany(type => Enum.GetValues<Reader>().Select(reader => new RunsCell(runs, fate, type, reader)))));

    private static IEnumerable<ResultCell> ResultCells() => new string?[] { null, "sequential-awaited" }.SelectMany(runs =>
        Results.SelectMany(result => Enum.GetValues<Reader>().Select(reader => new ResultCell(runs, result, reader)))).Where(Allowed);

    private static IEnumerable<ReceiverCell> ReceiverCells() => Enum.GetValues<Receiver>().SelectMany(receiver =>
        Enum.GetValues<Member>().SelectMany(member => Enum.GetValues<Reader>().Select(reader => new ReceiverCell(receiver, member, reader))));

    /// <summary>Excludes a form that the existing grammar cannot express, independently of the run relation.</summary>
    /// <param name="cell">The result cell.</param>
    private static bool Allowed(ResultCell cell) => cell.Result != "[new]"; // new is a result form, never a value in a result list.

    [Fact]
    public void There_are_540_runs_cells_and_every_axis_value_occurs()
    {
        var cells = RunsCells().ToArray();
        Assert.Equal(540, cells.Length);
        Assert.Equal(540, cells.Distinct().Count());
        Assert.All(Runs, runs => Assert.Contains(cells, cell => cell.Runs == runs));
        Assert.All(Fates, fate => Assert.Contains(cells, cell => cell.Fate == fate));
        Assert.All(Returns, type => Assert.Contains(cells, cell => cell.Return == type));
        Assert.All(Enum.GetValues<Reader>(), reader => Assert.Contains(cells, cell => cell.Reader == reader));
    }

    [Fact]
    public void Every_runs_cell_is_answered_exactly_and_accepted_cells_round_trip()
    {
        foreach (var cell in RunsCells())
        {
            var method = Method("D", "R" + Array.IndexOf(Returns, cell.Return));
            var fate = new JsonObject { ["fate"] = cell.Fate };
            if (cell.Fate == "holder")
                fate["holder"] = "this";
            if (cell.Runs is not null)
                fate["runs"] = cell.Runs;
            var entry = Entry(method);
            entry["fates"] = new JsonObject { ["f"] = fate };
            if (cell.Fate == "iterator")
                entry["result"] = "sequence(elements(arg:items))";
            var expected = cell.Runs is null || cell.Reader != Reader.Generated && cell.Fate == "invoke-now" &&
                (cell.Runs is "concurrent" or "sequential" || cell.Runs == "sequential-awaited" && cell.Return is not ("void" or "int"));

            Check(cell.ToString(), entry, method, cell.Reader, expected);
        }
    }

    [Fact]
    public void There_are_36_result_cells_and_every_allowed_axis_value_occurs()
    {
        var cells = ResultCells().ToArray();
        Assert.Equal(36, cells.Length);
        Assert.Equal(36, cells.Distinct().Count());
        Assert.All(new string?[] { null, "sequential-awaited" }, runs => Assert.Contains(cells, cell => cell.Runs == runs));
        Assert.All(Results.Where(result => result != "[new]"), result => Assert.Contains(cells, cell => cell.Result == result));
        Assert.All(Enum.GetValues<Reader>(), reader => Assert.Contains(cells, cell => cell.Reader == reader));
        Assert.False(Allowed(new ResultCell(null, "[new]", Reader.Project)));
    }

    [Fact]
    public void Every_result_cell_is_answered_exactly_and_accepted_cells_round_trip()
    {
        var method = Method("D", "M");
        foreach (var cell in ResultCells())
        {
            var fate = new JsonObject { ["fate"] = "invoke-now" };
            if (cell.Runs is not null)
                fate["runs"] = cell.Runs;
            var entry = Entry(method);
            entry["fates"] = new JsonObject { ["f"] = fate };
            if (cell.Result is not null)
                entry["result"] = cell.Result;
            if (cell.Result == "[kept:t]")
                entry["keeps"] = new JsonObject { ["t"] = new JsonArray("arg:t") };
            var expected = cell.Runs is null || cell.Reader != Reader.Generated &&
                cell.Result is null or "task([completion(returns:f)])";

            Check(cell.ToString(), entry, method, cell.Reader, expected);
        }
    }

    [Fact]
    public void There_are_162_receiver_cells_and_every_axis_value_occurs()
    {
        var cells = ReceiverCells().ToArray();
        Assert.Equal(162, cells.Length);
        Assert.Equal(162, cells.Distinct().Count());
        Assert.All(Enum.GetValues<Receiver>(), receiver => Assert.Contains(cells, cell => cell.Receiver == receiver));
        Assert.All(Enum.GetValues<Member>(), member => Assert.Contains(cells, cell => cell.Member == member));
        Assert.All(Enum.GetValues<Reader>(), reader => Assert.Contains(cells, cell => cell.Reader == reader));
    }

    [Fact]
    public void Every_receiver_cell_is_answered_exactly_and_accepted_cells_round_trip()
    {
        foreach (var cell in ReceiverCells())
        {
            var method = cell.Member switch
            {
                Member.Instance => Method("D", "Do"),
                Member.Getter => Method("D", "get_Value"),
                Member.Interface => Method("J", "Do"),
                Member.Static => Method("D", "Static"),
                Member.Constructor => Method("D", ".ctor"),
                Member.Extension => Method("Extensions", "Do"),
                _ => throw new ArgumentOutOfRangeException()
            };
            var entry = Entry(method);
            var receiver = ReceiverId(cell);
            if (receiver is not null)
                entry["receiver"] = receiver;
            var expected = cell.Receiver == Receiver.None || cell.Reader != Reader.Generated &&
                cell.Member is Member.Instance or Member.Getter or Member.Interface &&
                (cell.Receiver is Receiver.Declaring or Receiver.Derived ||
                 cell.Member == Member.Interface && cell.Receiver == Receiver.ImplementedInterface);

            Check(cell.ToString(), entry, method, cell.Reader, expected);
        }
    }

    [Fact]
    public void Generic_receiver_definitions_pass_both_curated_readers()
    {
        var method = Method("G`1", "Do");
        foreach (var reader in Enum.GetValues<Reader>())
        {
            foreach (var receiver in new[] { "T:Records.G`1", "T:Records.G2`1" })
            {
                var entry = Entry(method);
                entry["receiver"] = receiver;
                Check($"{reader} {receiver}", entry, method, reader, reader != Reader.Generated);
            }
        }
    }

    /// <summary>The concrete keys for class members, and the corresponding declaring, derived and implementing keys for J.</summary>
    /// <param name="cell">The receiver cell.</param>
    private static string? ReceiverId(ReceiverCell cell) => cell.Receiver switch
    {
        Receiver.None => null,
        Receiver.Declaring => cell.Member == Member.Interface ? "T:Records.J" : "T:Records.D",
        Receiver.Derived => cell.Member == Member.Interface ? "T:Records.J1" : "T:Records.K",
        Receiver.Object => "T:System.Object",
        Receiver.Unrelated => "T:Records.U",
        Receiver.ImplementedInterface => cell.Member == Member.Interface ? "T:Records.C" : "T:Records.I",
        Receiver.ForeignDerived => "T:Other.Foreign",
        Receiver.NoPrefix => "Polly.X",
        Receiver.Missing => "T:Records.Missing",
        _ => throw new ArgumentOutOfRangeException(nameof(cell))
    };

    /// <summary>Checks one exact answer and, when accepted, every field through the canonical writer and project reader.</summary>
    /// <param name="cell">The cell's description.</param>
    /// <param name="entry">The record's JSON.</param>
    /// <param name="method">The metadata member the record describes.</param>
    /// <param name="reader">The reader under test.</param>
    /// <param name="expected">Whether the record must be accepted.</param>
    private static void Check(string cell, JsonObject entry, IMethodSymbol method, Reader reader, bool expected)
    {
        var model = Read(entry, method, reader);
        Assert.True(expected == (model is not null), $"{cell}: expected {(expected ? "accepted" : "refused")}");
        if (model is null)
            return;
        Assert.Equal(entry["receiver"]?.GetValue<string>(), model.Receiver);
        var bytes = ModelEntryWriter.File(model);
        var file = ProjectModelFiles.Read("round-trip.json", bytes);
        Assert.Empty(file.Rejections);
        var restored = Assert.Single(file.Entries);
        Assert.Null(ProjectModelResolver.EntryRejection(restored, method, Fixture.Value));
        Assert.Equal(model.Id, restored.Member);
        Assert.Equal(model.Receiver, restored.Receiver);
        Assert.Equal(model.Assemblies.Select(range => range.AssemblyName), restored.Assemblies);
        Assert.All(model.Assemblies, range => Assert.Equal((range.Minimum, range.MaximumExclusive), restored.Versions));
        Assert.Equal(model.Effects, restored.Effects);
        Assert.True(LibraryVocabulary.Alike(model.Result, model.Fates, restored.Result, restored.Fates, model.Stores, restored.Stores,
                                            model.Outputs, restored.Outputs, model.Keeps, restored.Keeps), cell);
        Assert.True(LibraryVocabulary.AlikeCalls(model.Calls, restored.Calls), cell);
        Assert.Equal(bytes, ModelEntryWriter.File(Model(restored)));
        if (model.Fates.Count != 0)
        {
            var fate = Assert.Single(model.Fates);
            var raw = entry["fates"]![fate.Parameter]!.AsObject();
            var expectedRuns = raw["runs"]?.GetValue<string>() switch
            {
                "sequential" => RunRelation.Sequential,
                "sequential-awaited" => RunRelation.SequentialAwaited,
                _ => RunRelation.Concurrent
            };
            Assert.Equal(expectedRuns, fate.Runs);
            Assert.Equal(expectedRuns != RunRelation.Concurrent, ModelEntryWriter.Write(model)["fates"]![fate.Parameter]!.AsObject().ContainsKey("runs"));
        }
    }

    /// <summary>Reads the file form, then checks its member against metadata, as the resolver does.</summary>
    /// <param name="entry">The record's JSON.</param>
    /// <param name="method">The metadata member.</param>
    /// <param name="reader">The file form.</param>
    private static LibraryModel? Read(JsonObject entry, IMethodSymbol method, Reader reader)
    {
        LibraryModel model;
        if (reader == Reader.BuiltIn)
        {
            try
            {
                model = Assert.Single(BuiltInModelReader.Read(Encoding.UTF8.GetBytes(
                    "{\"schemaVersion\":1,\"models\":[" + entry.ToJsonString() + "]}")).Members);
            }
            catch (LibraryModelException)
            {
                return null;
            }
        }
        else
        {
            ProjectModelEntry? parsed;
            if (reader == Reader.Project)
            {
                var file = ProjectModelFiles.Read("matrix.json", Encoding.UTF8.GetBytes(
                    "{\"schemaVersion\":1,\"models\":[" + entry.ToJsonString() + "]}"));
                parsed = file.Entries.SingleOrDefault();
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":2,\"generator\":\"" + GeneratorVersion.Fingerprint +
                    "\",\"assembly\":{\"name\":\"Records\",\"version\":\"1.0.0.0\",\"mvid\":\"" + Key.Mvid +
                    "\",\"package\":\"Records\",\"packageVersion\":\"1.0.0\"},\"members\":[{\"member\":\"" +
                    DocumentationCommentId.CreateDeclarationId(method) + "\",\"model\":" + entry.ToJsonString() + "}]}");
                parsed = GeneratedModelFiles.Valid(Key, "generated.json", bytes).Entries.Values.SingleOrDefault()?.Model;
            }
            if (parsed is null || ProjectModelResolver.EntryRejection(parsed, method, Fixture.Value) is not null)
                return null;
            model = Model(parsed);
        }
        return LibraryVocabulary.Member(model.Result, model.Fates, method, Fixture.Value, model.Effects, model.Stores, model.Outputs,
                                        model.Keeps, model.Receiver) is null ? model : null;
    }

    /// <summary>Copies a parsed project record into the model the writer consumes.</summary>
    /// <param name="entry">The parsed record.</param>
    private static LibraryModel Model(ProjectModelEntry entry) => new(entry.Member,
        entry.Assemblies.Select(assembly => new SupportedAssemblyVersion(assembly, entry.Versions!.Value.Minimum, entry.Versions.Value.Maximum)).ToArray(),
        entry.Effects, Receiver: entry.Receiver)
    {
        Result = entry.Result, Fates = entry.Fates, Stores = entry.Stores, Outputs = entry.Outputs, Keeps = entry.Keeps, Calls = entry.Calls
    };

    /// <summary>A baseline record whose fate and receiver can vary independently.</summary>
    /// <param name="method">The metadata member.</param>
    private static JsonObject Entry(IMethodSymbol method) => new()
    {
        ["member"] = DocumentationCommentId.CreateDeclarationId(method),
        ["assemblies"] = new JsonArray("Records"),
        ["versions"] = new JsonObject { ["minimum"] = "1.0.0.0", ["maximumExclusive"] = "2.0.0.0" },
        ["effects"] = new JsonObject()
    };

    /// <summary>The member of the metadata fixture.</summary>
    /// <param name="type">The type's metadata name within Records.</param>
    /// <param name="name">The member's name.</param>
    private static IMethodSymbol Method(string type, string name) =>
        Fixture.Value.GetTypeByMetadataName("Records." + type)!.GetMembers(name).OfType<IMethodSymbol>().Single();

    private static Compilation Compile()
    {
        var methods = string.Join('\n', Returns.Select((type, index) =>
            $"public IEnumerable<object> R{index}({(type == "void" ? "Action" : $"Func<{type}>")} f, IEnumerable<object> items) => null!;"));
        var library = CSharpCompilation.Create("Records", [CSharpSyntaxTree.ParseText("""
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            [assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
            namespace Records;
            public interface I { }
            public interface J { void Do(); }
            public interface J1 : J { }
            public class C : J { public void Do() { } }
            public class U { }
            public class G<T> { public void Do() { } }
            public class G2<T> : G<T> { }
            public class K : D { }
            public class D : I
            {
                public void Do() { }
                public object Value => null!;
                public static void Static() { }
                public Task<int> M(Func<Task<int>> f, Task<int> t) => null!;
            """ + methods + "\n}\npublic static class Extensions { public static void Do(this D receiver) { } }")],
            StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var reference = Emit(library);
        var foreign = CSharpCompilation.Create("Other", [CSharpSyntaxTree.ParseText("namespace Other; public class Foreign : Records.D { }")],
            StubAssemblies.PlatformWithout([]).Append(reference), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return CSharpCompilation.Create("Consumer", [], StubAssemblies.PlatformWithout([]).Append(reference).Append(Emit(foreign)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>Compiles a fixture library so every member check reads metadata rather than source.</summary>
    /// <param name="compilation">The fixture library.</param>
    private static MetadataReference Emit(CSharpCompilation compilation)
    {
        using var bytes = new MemoryStream();
        var result = compilation.Emit(bytes);
        Assert.True(result.Success, string.Join('\n', result.Diagnostics));
        return MetadataReference.CreateFromImage(bytes.ToArray());
    }
}
