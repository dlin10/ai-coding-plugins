using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The members a known call runs on the user's objects (<c>calls</c>, question 47): an override runs where the call stands,
/// in the caller's execution, with what the model hands each parameter.</summary>
public sealed class ModelCallTests
{
    [Fact]
    public void A_users_GetHashCode_the_call_runs_writes_where_the_call_stands()
    {
        var run = Run("_ = Lib.Hash(_state.First);");

        Assert.Contains(run.Of("Hashed"), access => access.Operation == AccessOperation.Write);
    }

    [Fact]
    public void A_users_Equals_writes_the_other_object_its_input_names()
    {
        // Equals runs on First and writes the object handed to it: a deep read alone would only read.
        var run = Run("_ = Lib.Same(_state.First, _state.Second);");

        Assert.Contains(run.Of("Compared"), access => access.Operation == AccessOperation.Write);
    }

    [Fact]
    public void A_users_CompareTo_of_IComparable_of_T_runs()
    {
        var run = Run("_ = Lib.Order(_state.First, _state.Second);");

        Assert.Contains(run.Of("Ranked"), access => access.Operation == AccessOperation.Write);
    }

    [Fact]
    public void A_type_without_the_member_runs_nothing_and_leaves_no_gap()
    {
        var run = Run("_ = Lib.Hash(_state.Plain);");

        Assert.DoesNotContain(run.Of("Hashed"), access => access.Operation == AccessOperation.Write);
        Assert.Empty(run.Collection.Coverage.Gaps);
    }

    [Fact]
    public void A_call_the_model_does_not_name_runs_no_member()
    {
        var run = Run("_ = Lib.Unnamed(_state.First);");

        Assert.DoesNotContain(run.Of("Hashed"), access => access.Operation == AccessOperation.Write);
    }

    private const string MODELS = """
        {"schemaVersion":1,"assemblies":["Calls"],"models":[
          {"member":"M:Calls.Lib.Hash``1(``0)","effects":{"value":["reads-deep"]},"calls":[{"member":"GetHashCode","on":"arg:value"}]},
          {"member":"M:Calls.Lib.Unnamed``1(``0)","effects":{"value":["reads-deep"]}},
          {"member":"M:Calls.Lib.Same(System.Object,System.Object)","effects":{"a":["reads-deep"],"b":["reads-deep"]},
           "calls":[{"member":"Equals","on":"arg:a","inputs":[["arg:b"]]}]},
          {"member":"M:Calls.Lib.Order``1(``0,``0)","effects":{"a":["reads-deep"],"b":["reads-deep"]},
           "calls":[{"member":"CompareTo","on":"arg:a","inputs":[["arg:b"]]}]}
        ]}
        """;

    private static readonly Lazy<MetadataReference> Library = new(() =>
    {
        var compilation = CSharpCompilation.Create("Calls", [CSharpSyntaxTree.ParseText("""
            namespace Calls;
            public static class Lib
            {
                public static int Hash<T>(T value) => 0;
                public static int Unnamed<T>(T value) => 0;
                public static bool Same(object a, object b) => false;
                public static int Order<T>(T a, T b) => 0;
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        var emit = compilation.Emit(bytes);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return MetadataReference.CreateFromImage(bytes.ToArray());
    });

    private static EngineRun Run(string work)
    {
        var solution = Solution(work);
        return AnalyzeScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, MODELS));
    }

    private static Solution Solution(string work)
    {
        return FixtureSolution.Create(new FixtureOptions { MetadataReferences = [Library.Value] }, ("Case.cs", Usings + $$"""
            using Calls;
            public class Item : System.IComparable<Item>
            {
                public bool Hashed;
                public int Ranked;
                public bool Compared;
                public override int GetHashCode() { Hashed = true; return 1; }
                public override bool Equals(object? other) { if (other is Item item) item.Compared = true; return false; }
                public int CompareTo(Item? other) { Ranked = 1; return 0; }
            }
            public class Plain { public bool Hashed; }
            public sealed class State
            {
                public Item First = new Item();
                public Item Second = new Item();
                public Plain Plain = new Plain();
            }
            public sealed class Worker(State state) : BackgroundService
            {
                private readonly State _state = state;
                protected override Task ExecuteAsync(CancellationToken stoppingToken) { {{work}} return Task.CompletedTask; }
            }
            """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();")));
    }
}
