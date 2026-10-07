using System.Text;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The forms of a task's producer that <c>CompletionObservationMatrixTests</c> gives in their simplest form only (R6): a
/// <c>WhenAll</c> or <c>WhenAny</c> given a collection rather than a list, and a project model whose <c>task(…)</c> result names a
/// <c>completion(…)</c> of a task argument at depth 1 and 2, what a delegate returned, or what a keeper keeps. Each cell's generated result
/// is its synchronous twin's inside <c>task(…)</c>; a cell whose producer is handed an unseen value names no result with the input.</summary>
public sealed class CompletionProducerFormTests
{
    private const string DEPENDENCY = "Fixture.Forms";
    private const string MEMBER = "M:Lib.Cell.Run(Lib.Box,System.Boolean)";

    private const string TYPES = """
        public static class Externals
        {
            public static extern Box MakeBox();
        }

        """;

    private static readonly Lazy<MetadataReference> Dependency = new(CompileDependency);

    /// <summary>A cell's member body and its twin's, each a return statement of <c>Box Run(Box x, bool flag)</c> or its awaited form.</summary>
    /// <param name="cell">The cell's statements, ending in the return of the awaited value.</param>
    /// <param name="twin">The twin's statements, returning the value directly.</param>
    [Theory]
    [InlineData("var tasks = new[] { Task.FromResult(x) }; return (await Task.WhenAll(tasks))[0];", "var items = new[] { x }; return items[0];")]
    [InlineData("var tasks = new List<Task<Box>> { Task.FromResult(x) }; return (await Task.WhenAll(tasks))[0];", "var items = new[] { x }; return items[0];")]
    [InlineData("var tasks = new[] { Task.FromResult(x) }; return await await Task.WhenAny(tasks);", "var items = new[] { x }; return items[0];")]
    [InlineData("var tasks = new List<Task<Box>> { Task.FromResult(x) }; return await await Task.WhenAny(tasks);", "var items = new[] { x }; return items[0];")]
    [InlineData("return await Forms.Models.PassTask(Task.FromResult(x));", "return Forms.Models.PassSync(x);")]
    [InlineData("return await Forms.Models.PassTaskTask(Task.FromResult(Task.FromResult(x)));", "return Forms.Models.PassSync(x);")]
    [InlineData("return await Forms.Models.Invoke(() => x);", "return Forms.Models.InvokeSync(() => x);")]
    [InlineData("var k = new Forms.Keeper(); Forms.Models.Keep(k, x); return await Forms.Models.Kept<Box>(k);",
                "var k = new Forms.Keeper(); Forms.Models.Keep(k, x); return Forms.Models.KeptSync<Box>(k);")]
    [InlineData("var k = new Forms.Keeper(); Forms.Models.Keep(k, flag ? x : Externals.MakeBox()); return await Forms.Models.Kept<Box>(k);",
                "var k = new Forms.Keeper(); Forms.Models.Keep(k, flag ? x : Externals.MakeBox()); return Forms.Models.KeptSync<Box>(k);")]
    public void A_producer_form_generates_its_synchronous_twins_result(string cell, string twin)
    {
        var expected = Generate($"public static Box Run(Box x, bool flag) {{ {twin} }}").Answer.Model?.Result?.ToString();
        var trace = Generate($"public static async Task<Box> Run(Box x, bool flag) {{ {cell} }}");
        var result = trace.Answer.Model?.Result?.ToString();

        Assert.NotNull(expected);
        Assert.True($"task({expected})" == result, $"the twin gives {expected}, the cell {result ?? "no result"} " +
                                                   $"({trace.Answer.Reason ?? trace.Answer.ModelReason}: {trace.Answer.Detail})");
    }

    /// <summary>Where the walk stops: the same forms handed an unseen value name no result with the input. A keeper is not among them:
    /// its synchronous twin <c>[kept:k]</c> names what the keeper's storage holds whatever else was kept, and the cell gives the twin's
    /// answer (<see cref="A_producer_form_generates_its_synchronous_twins_result"/>).</summary>
    /// <param name="cell">The cell's statements, ending in the return of the awaited value.</param>
    [Theory]
    [InlineData("var tasks = new[] { Task.FromResult(flag ? x : Externals.MakeBox()) }; return (await Task.WhenAll(tasks))[0];")]
    [InlineData("var tasks = new[] { Task.FromResult(flag ? x : Externals.MakeBox()) }; return await await Task.WhenAny(tasks);")]
    [InlineData("return await Forms.Models.PassTaskTask(Task.FromResult(Task.FromResult(flag ? x : Externals.MakeBox())));")]
    [InlineData("return await Forms.Models.Invoke(() => flag ? x : Externals.MakeBox());")]
    public void A_producer_form_handed_an_unseen_value_names_no_result_with_the_input(string cell)
    {
        var trace = Generate($"public static async Task<Box> Run(Box x, bool flag) {{ {cell} }}");

        Assert.DoesNotContain("arg:x", trace.Answer.Model?.Result?.ToString() ?? "", StringComparison.Ordinal);
    }

    /// <summary>The generator's trace for <see cref="MEMBER"/> of a library of the prelude, <see cref="TYPES"/> and the member, which
    /// references the dependency with its project models.</summary>
    /// <param name="member">The member's declaration.</param>
    private static GenerationTrace Generate(string member)
    {
        var source = GenerationRuns.PRELUDE + TYPES + $"public static class Cell {{ {member} }}" + "\n}\n";
        var library = LibraryCompilation.CompileTrees(GenerationRuns.ASSEMBLY,
                                                      [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), $"{GenerationRuns.ASSEMBLY}/Library.cs")],
                                                      [.. EmittedAssemblies.RuntimeReferences, Dependency.Value], CancellationToken.None);
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        var trace = ModelGenerator.Trace(new GenerationRequest(GenerationRuns.ASSEMBLY, "1.0", MEMBER, null, null), library, CancellationToken.None,
                                         DependencyModels(library.Compilation));
        Assert.True(trace.Answer.Reason is null, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        return trace;
    }

    private static MetadataReference CompileDependency()
    {
        const string SOURCE = """
            using System;
            using System.Threading.Tasks;
            namespace Forms
            {
                public sealed class Keeper { }
                public static class Models
                {
                    public static T PassSync<T>(T x) => x;
                    public static Task<T> PassTask<T>(Task<T> t) => t;
                    public static Task<T> PassTaskTask<T>(Task<Task<T>> t) => null!;
                    public static Task<T> Invoke<T>(Func<T> f) => Task.FromResult(f());
                    public static T InvokeSync<T>(Func<T> f) => f();
                    public static void Keep<T>(Keeper k, T x) { }
                    public static Task<T> Kept<T>(Keeper k) => null!;
                    public static T KeptSync<T>(Keeper k) => default!;
                }
            }
            """;
        var compilation = CSharpCompilation.Create(DEPENDENCY, [CSharpSyntaxTree.ParseText(SOURCE)], EmittedAssemblies.RuntimeReferences,
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    /// <summary>The built-in models and the dependency's project models: each task member says what its task completes with by the form
    /// its name gives, beside the synchronous twin naming the same value.</summary>
    /// <param name="compilation">The library's compilation, which references the dependency.</param>
    private static LibraryModels DependencyModels(Compilation compilation)
    {
        string Entry(string member, string body) => $"{{\"member\":\"M:Forms.Models.{member}\",\"effects\":{{}},{body}}}";
        var models = new[]
        {
            Entry("PassSync``1(``0)", "\"result\":\"[arg:x]\""),
            Entry("PassTask``1(System.Threading.Tasks.Task{``0})", "\"result\":\"task([completion(arg:t)])\""),
            Entry("PassTaskTask``1(System.Threading.Tasks.Task{System.Threading.Tasks.Task{``0}})", "\"result\":\"task([completion(completion(arg:t))])\""),
            Entry("Invoke``1(System.Func{``0})", "\"result\":\"task([returns:f])\",\"fates\":{\"f\":{\"fate\":\"invoke-now\",\"inputs\":[]}}"),
            Entry("InvokeSync``1(System.Func{``0})", "\"result\":\"[returns:f]\",\"fates\":{\"f\":{\"fate\":\"invoke-now\",\"inputs\":[]}}"),
            Entry("Keep``1(Forms.Keeper,``0)", "\"keeps\":{\"k\":[\"arg:x\"]}"),
            Entry("Kept``1(Forms.Keeper)", "\"result\":\"task([kept:k])\""),
            Entry("KeptSync``1(Forms.Keeper)", "\"result\":\"[kept:k]\"")
        };
        var text = "{\"schemaVersion\":1,\"assemblies\":[\"" + DEPENDENCY + "\"],\"models\":[" + string.Join(",", models) + "]}";
        var root = Directory.CreateTempSubdirectory("ch-producer-forms-").FullName;
        try
        {
            var folder = Path.Combine(root, ".concurrency-hunter", "models");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "forms.json"), text, new UTF8Encoding(false));
            var files = ProjectModelFiles.Read(root);
            var (resolved, rejections) = ProjectModelResolver.Resolve(files, [compilation], ModelLock.Read(root, files));
            Assert.True(rejections.Count == 0, string.Join("\n", rejections));
            return resolved;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
