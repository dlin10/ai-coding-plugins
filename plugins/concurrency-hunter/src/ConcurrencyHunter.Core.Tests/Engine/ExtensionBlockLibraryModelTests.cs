using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A model of a member of an extension block in a referenced library (issue 511): the model names the implementation the
/// compiler emits, whose first parameter is the receiver and whose others are the member's one further on, so a call of the member
/// binds each effect to the argument the implementation takes there, the receiver among them, as the same call written in the static
/// form does.</summary>
public sealed class ExtensionBlockLibraryModelTests
{
    /// <summary>A project model of the library: <c>Wipe</c> writes its receiver's cells, <c>CopyInto</c> those of its argument, and
    /// <c>Lend</c> hands back a new array through its <c>out</c> parameter.</summary>
    private const string MODELS = """
        {"schemaVersion":1,"assemblies":["Shelves"],"models":[
          {"member":"M:Shelves.Arrays.Wipe(System.Int32[])","effects":{"array":["writes-cells"]}},
          {"member":"M:Shelves.Arrays.CopyInto(System.Int32[],System.Int32[])","effects":{"other":["writes-cells"]}},
          {"member":"M:Shelves.Arrays.Lend(System.Int32[],System.Int32[]@)","effects":{},"outputs":{"lent":"new"}}
        ]}
        """;

    /// <summary>The library: one extension block over <c>int[]</c>.</summary>
    private static readonly Lazy<MetadataReference> Library = new(() =>
    {
        var compilation = CSharpCompilation.Create("Shelves", [CSharpSyntaxTree.ParseText("""
            namespace Shelves;
            public static class Arrays
            {
                extension(int[] array)
                {
                    public void Wipe() { }
                    public void CopyInto(int[] other) { }
                    public void Lend(out int[] lent) => lent = array;
                }
            }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        var emit = compilation.Emit(bytes);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return MetadataReference.CreateFromImage(bytes.ToArray());
    });

    /// <summary>A call of a library block member, in the member form or the static form, writes the cells its model names: the
    /// receiver's for <c>Wipe</c>, the argument's for <c>CopyInto</c>, and no other array's.</summary>
    /// <param name="work">The statement the worker runs.</param>
    /// <param name="written">The field holding the array whose cells the call writes.</param>
    /// <param name="untouched">The field holding the array the call leaves alone.</param>
    [Theory]
    [InlineData("_state.First.Wipe();", "First", "Second")]
    [InlineData("Shelves.Arrays.Wipe(_state.First);", "First", "Second")]
    [InlineData("_state.First.CopyInto(_state.Second);", "Second", "First")]
    [InlineData("Shelves.Arrays.CopyInto(_state.First, _state.Second);", "Second", "First")]
    public void A_block_members_model_writes_the_cells_of_the_argument_its_implementation_names(string work, string written, string untouched)
    {
        var solution = Solution(work);

        var run = AnalyzeScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, MODELS));

        Assert.Contains(run.Of(written), access => access.Resource.Selector is not null && access.Operation == AccessOperation.Write);
        Assert.DoesNotContain(run.Of(untouched), access => access.Resource.Selector is not null && access.Operation == AccessOperation.Write);
    }

    /// <summary>A call of a library block member, in either form, writes the field its <c>out</c> argument names with what its model's
    /// output gives: the lowering of the argument and of the call's output agree on the model, the implementation's.</summary>
    /// <param name="work">The statement the worker runs.</param>
    [Theory]
    [InlineData("_state.First.Lend(out _state.Slot);")]
    [InlineData("Shelves.Arrays.Lend(_state.First, out _state.Slot);")]
    public void A_block_members_model_output_writes_the_out_argument(string work)
    {
        var solution = Solution(work);

        var run = AnalyzeScope(solution, "scope:Fixture", ModelCellFixture.Resolve(solution, MODELS));

        Assert.Contains(run.Of("Slot"), access => access.Operation == AccessOperation.Write);
    }

    /// <summary>A worker that runs a statement on a singleton holding two arrays.</summary>
    /// <param name="work">The statement.</param>
    private static Solution Solution(string work) => FixtureSolution.Create(
        new FixtureOptions { MetadataReferences = [Library.Value] }, ("Case.cs", Usings + $$"""
        using Shelves;
        public sealed class State
        {
            public readonly int[] First = new int[2];
            public readonly int[] Second = new int[2];
            public int[] Slot = new int[1];
        }
        public sealed class Worker(State state) : BackgroundService
        {
            private readonly State _state = state;
            protected override Task ExecuteAsync(CancellationToken stoppingToken) { {{work}} return Task.CompletedTask; }
        }
        """ + Startup("services.AddSingleton<State>(); services.AddHostedService<Worker>();")));
}
