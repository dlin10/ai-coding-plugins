using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class NewInputVocabularyTests
{
    [Theory]
    [InlineData("Second")]
    [InlineData("First")]
    public void Each_registered_holder_fate_place_gets_its_own_new_input(string second)
    {
        var run = ModelVocabularyFixture.Run($$"""
            var holder = new NewInputs.Holder();
            holder.First(first => first.Value = 1);
            holder.{{second}}(second => second.Value = 2);
            holder.Run();
            """, ModelVocabularyFixture.Entry("Holder.First", Fate("holder", "new", "this")),
                 ModelVocabularyFixture.Entry("Holder.Second", Fate("holder", "new", "this")));

        var worker = Assert.Single(run.Instances("body:Fixture:M:Worker.ExecuteAsync(System.Threading.CancellationToken)"));
        var call = Assert.Single(worker.Summary.OpaqueCalls, call => call.Callee.Contains("NewInputs.Holder.Run", StringComparison.Ordinal));
        var first = InputAt("#lambda1");
        var other = InputAt("#lambda2");
        Assert.NotEqual(first.Identity, other.Identity);
        Assert.Equal(first.SiteOperationId, other.SiteOperationId);
        Assert.Equal(first.TypeKey, other.TypeKey);

        HeapRegion InputAt(string lambda)
        {
            var instance = Assert.Single(run.Heap.Instances.Values, instance => instance.BodyId.EndsWith(lambda, StringComparison.Ordinal));
            return Assert.Single(instance.Parameters[0].Select(region => run.Heap.Regions[region]), region => region.SiteOperationId == call.OperationId);
        }
    }

    [Fact]
    public void New_is_parsed_as_a_value() => Assert.IsType<NewValue>(LibraryValue.Parse("new"));

    [Fact]
    public void New_at_an_input_top_level_is_accepted() => Accepted("Invoke", Fate("invoke-now", "new"));

    [Fact]
    public void New_at_an_input_top_level_is_accepted_beside_another_value() =>
        Accepted("Keep", "\"effects\":{},\"fates\":{\"callback\":{\"fate\":\"invoke-now\",\"inputs\":[[\"new\",\"arg:value\"]]}}");

    [Fact]
    public void New_inside_elements_is_refused() => Refused("Invoke", Fate("invoke-now", "elements(new)"), "elements");

    [Fact]
    public void New_inside_sequence_is_refused() => Refused("Invoke", Fate("invoke-now", "sequence(new)"), "sequence");

    [Fact]
    public void New_inside_grouping_is_refused() => Refused("Invoke", Fate("invoke-now", "grouping(new,arg:value)"), "grouping");

    [Fact]
    public void New_inside_a_result_list_is_refused() =>
        Refused("Invoke", "\"effects\":{},\"result\":\"[new]\",\"fates\":{\"callback\":{\"fate\":\"invoke-now\",\"inputs\":[[]]}}", "result list");

    [Fact]
    public void New_inside_keeps_is_refused() =>
        Refused("Keep", "\"effects\":{},\"keeps\":{\"value\":[\"new\"]},\"fates\":{\"callback\":{\"fate\":\"invoke-now\",\"inputs\":[[]]}}", "keeps");

    [Fact]
    public void New_inside_stores_is_refused() =>
        Refused("Store", "\"effects\":{\"target\":[\"writes-cells\"]},\"stores\":{\"target\":[\"new\"]},\"fates\":{\"callback\":{\"fate\":\"invoke-now\",\"inputs\":[[]]}}", "stores");

    [Fact]
    public void New_inside_outputs_is_refused() =>
        Refused("Output", "\"effects\":{},\"outputs\":{\"output\":\"[new]\"},\"fates\":{\"callback\":{\"fate\":\"invoke-now\",\"inputs\":[[]]}}", "outputs");

    [Fact]
    public void New_for_a_value_typed_parameter_is_refused() => Refused("Value", Fate("invoke-now", "new"), "reference-typed parameter");

    [Fact]
    public void New_for_an_unconstrained_type_parameter_is_refused() => Refused("Generic", Fate("invoke-now", "new"), "reference-typed parameter");

    [Fact]
    public void New_for_a_notnull_type_parameter_is_refused() => Refused("NotNull", Fate("invoke-now", "new"), "reference-typed parameter");

    [Fact]
    public void New_for_a_struct_type_parameter_is_refused() => Refused("Struct", Fate("invoke-now", "new"), "reference-typed parameter");

    [Fact]
    public void New_for_a_class_constrained_type_parameter_is_accepted() => Accepted("Class", Fate("invoke-now", "new"));

    [Fact]
    public void New_for_a_delegate_constrained_type_parameter_is_refused() =>
        Refused("DelegateConstraint", Fate("invoke-now", "new"), "fresh delegate");

    [Fact]
    public void New_for_an_interface_typed_parameter_is_accepted() => Accepted("Interface", Fate("invoke-now", "new"));

    [Fact]
    public void New_for_an_array_typed_parameter_is_accepted() => Accepted("Array", Fate("invoke-now", "new"));

    [Fact]
    public void New_for_a_delegate_typed_parameter_is_refused() => Refused("Delegate", Fate("invoke-now", "new"), "fresh delegate");

    [Fact]
    public void New_in_an_iterator_input_is_refused() =>
        Refused("Iterate", "\"effects\":{},\"result\":\"sequence(arg:source)\",\"fates\":{\"callback\":{\"fate\":\"iterator\",\"inputs\":[[\"new\"]]}}", "invoke-now or holder fate");

    [Fact]
    public void New_in_a_startup_input_is_refused() => Refused("Invoke", Fate("startup", "new"), "invoke-now or holder fate");

    [Fact]
    public void New_in_an_unknown_execution_input_is_refused() => Refused("Invoke", Fate("unknown-execution", "new"), "invoke-now or holder fate");

    [Fact]
    public void Invoke_now_gets_a_fresh_object_and_two_call_sites_get_two_objects()
    {
        var run = ModelVocabularyFixture.Run("""
            NewInputs.Lib.Invoke(first => first.Value = 1);
            NewInputs.Lib.Invoke(second => second.Value = 2);
            """, ModelVocabularyFixture.Entry("Invoke", Fate("invoke-now", "new")));

        var first = Input(run, "#lambda1");
        var second = Input(run, "#lambda2");
        Assert.NotEqual(first.Identity, second.Identity);
        Assert.Equal("NewInputs:NewInputs.Payload", first.TypeKey);
        Assert.Equal("NewInputs:NewInputs.Payload", second.TypeKey);
        Assert.NotEqual(first.SiteOperationId, second.SiteOperationId);
    }

    [Fact]
    public void Holder_gets_a_fresh_object_at_the_holder_member_call()
    {
        var run = ModelVocabularyFixture.Run("""
            var holder = NewInputs.Lib.Hold(null!, value => value.Value = 1);
            holder.Run();
            """, ModelVocabularyFixture.Entry("Hold", Fate("holder", "new", "result")));

        var worker = Assert.Single(run.Instances("body:Fixture:M:Worker.ExecuteAsync(System.Threading.CancellationToken)"));
        var memberCall = Assert.Single(worker.Summary.OpaqueCalls, call => call.Callee.Contains("NewInputs.Holder.Run", StringComparison.Ordinal));
        var holdingCall = Assert.Single(worker.Summary.OpaqueCalls, call => call.Callee.Contains("NewInputs.Lib.Hold", StringComparison.Ordinal));
        var input = Input(run, "#lambda1");
        Assert.Equal(memberCall.OperationId, input.SiteOperationId);
        Assert.NotEqual(holdingCall.OperationId, input.SiteOperationId);
    }

    private static HeapRegion Input(HeapRun run, string lambda)
    {
        var instance = Assert.Single(run.Heap.Instances.Values, instance => instance.BodyId.EndsWith(lambda, StringComparison.Ordinal));
        var region = Assert.Single(instance.Parameters[0]);
        return run.Heap.Regions[region];
    }

    private static string Fate(string fate, string input, string? holder = null) =>
        "\"effects\":{},\"fates\":{\"callback\":{\"fate\":\"" + fate + "\"" +
        (holder is null ? "" : ",\"holder\":\"" + holder + "\"") + ",\"inputs\":[[\"" + input + "\"]]}}";

    private static void Accepted(string method, string decision)
    {
        var (_, rejections) = ModelVocabularyFixture.Resolve(ModelVocabularyFixture.Entry(method, decision));
        Assert.Empty(rejections);
    }

    private static void Refused(string method, string decision, string reason)
    {
        var (_, rejections) = ModelVocabularyFixture.Resolve(ModelVocabularyFixture.Entry(method, decision));
        Assert.Contains(reason, Assert.Single(rejections).Reason, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class ModelVocabularyFixture
{
    private static readonly Lazy<(CSharpCompilation Compilation, MetadataReference Reference)> Library = new(CreateLibrary);

    internal static string Entry(string method, string decision) =>
        "{\"member\":\"" + Id(method) + "\"," + decision + "}";

    internal static (LibraryModels Models, IReadOnlyList<ModelRejection> Rejections) Resolve(params string[] entries)
    {
        var solution = Solution("");
        using var repo = new CellModelRepository(File(entries));
        var compilation = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        var files = ProjectModelFiles.Read(repo.Root);
        return ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(repo.Root, files));
    }

    internal static HeapRun Run(string work, params string[] entries)
    {
        var solution = Solution(work);
        using var repo = new CellModelRepository(File(entries));
        var compilation = solution.Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        var files = ProjectModelFiles.Read(repo.Root);
        var (models, rejections) = ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(repo.Root, files));
        Assert.Empty(rejections);
        return EngineFixture.Solve(EngineFixture.ReachScope(solution, "scope:Fixture", models));
    }

    private static string File(IEnumerable<string> entries) =>
        "{\"schemaVersion\":1,\"assemblies\":[\"NewInputs\"],\"models\":[" + string.Join(',', entries) + "]}";

    private static string Id(string method)
    {
        var parts = method.Split('.');
        var type = parts.Length == 1 ? "Lib" : parts[0];
        var symbol = Library.Value.Compilation.GetTypeByMetadataName("NewInputs." + type)!.GetMembers(parts[^1]).Single();
        return DocumentationCommentId.CreateDeclarationId(symbol)!;
    }

    private static Solution Solution(string work) => FixtureSolution.Create(
        new FixtureOptions { MetadataReferences = [Library.Value.Reference] },
        ("Case.cs", Usings + "using NewInputs;\n" + $$"""
            public sealed class Worker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    {{work}}
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddHostedService<Worker>();")));

    private static (CSharpCompilation Compilation, MetadataReference Reference) CreateLibrary()
    {
        var compilation = CSharpCompilation.Create("NewInputs", [CSharpSyntaxTree.ParseText("""
            #nullable enable
            using System;
            using System.Collections.Generic;

            namespace NewInputs;

            public class Payload { public int Value; }
            public interface IPayload { }
            public delegate void PayloadDelegate();
            public sealed class Holder
            {
                public void Run() { }
                public void First(Action<Payload> callback) { }
                public void Second(Action<Payload> callback) { }
            }

            public static class Lib
            {
                public static void Invoke(Action<Payload> callback) { }
                public static Holder Hold(Payload value, Action<Payload> callback) => null!;
                public static IEnumerable<Payload> Iterate(IEnumerable<Payload> source, Action<Payload> callback) => null!;
                public static void Keep(Payload value, Action<Payload> callback) { }
                public static void Store(Payload[] target, Action<Payload> callback) { }
                public static void Output(out Payload output, Action<Payload> callback) => output = null!;
                public static void Value(Action<int> callback) { }
                public static void Interface(Action<IPayload> callback) { }
                public static void Array(Action<Payload[]> callback) { }
                public static void Delegate(Action<PayloadDelegate> callback) { }
                public static void Generic<T>(Action<T> callback) { }
                public static void NotNull<T>(Action<T> callback) where T : notnull { }
                public static void Struct<T>(Action<T> callback) where T : struct { }
                public static void Class<T>(Action<T> callback) where T : class { }
                public static void DelegateConstraint<T>(Action<T> callback) where T : System.Delegate { }
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        using var bytes = new MemoryStream();
        var emit = compilation.Emit(bytes);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        return (compilation, MetadataReference.CreateFromImage(bytes.ToArray()));
    }
}
