using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>R1: the two built-in Polly 7 retry records are chosen by the receiver's static type, against the real <c>Polly.dll</c>, and
/// an assembly named <c>Polly</c> outside 7.0.0.0 up to 8.0.0.0 takes none and is reported.</summary>
public sealed class PollyRetryRecordTests
{
    private const string POLLY = "Polly";
    private const string PRELUDE = "using System; using System.Threading; using System.Threading.Tasks; using Polly; using Polly.Retry;";

    private static readonly Lazy<MetadataReference> RealPolly = new(() => MetadataReference.CreateFromFile(Path.Combine(AppContext.BaseDirectory, "Polly.dll")));

    [Theory]
    [InlineData("var p = Policy.Handle<Exception>().Retry(2); p.Execute(() => { });", "sequential")]
    [InlineData("RetryPolicy p = Policy.Handle<Exception>().WaitAndRetry(2, _ => TimeSpan.Zero); p.Execute(() => { });", "sequential")]
    [InlineData("var p = Policy.Handle<Exception>().RetryAsync(2); p.ExecuteAsync(() => Task.CompletedTask);", "sequential-awaited")]
    [InlineData("AsyncRetryPolicy p = Policy.Handle<Exception>().WaitAndRetryAsync(2, _ => TimeSpan.Zero); p.ExecuteAsync(() => Task.CompletedTask);",
                "sequential-awaited")]
    public void Keyed_receiver_reads_its_record(string source, string runs)
    {
        var match = Find(source, [RealPolly.Value]);

        Assert.NotNull(match);
        Assert.Equal((LibraryMatchKind.Known, ModelLayer.BuiltIn), (match.Kind, match.Layer));
        var fate = Assert.Single(match.Fates);
        Assert.Equal(("action", "invoke-now", runs), (fate.Parameter, LibraryFate.Text(fate.Kind), LibraryFate.Text(fate.Runs)));
        Assert.Empty(match.Effects);
    }

    [Theory]
    [InlineData("Policy p = Policy.Handle<Exception>().Retry(2); p.Execute(() => { });")]
    [InlineData("ISyncPolicy p = Policy.Handle<Exception>().Retry(2); p.Execute(() => { });")]
    [InlineData("AsyncPolicy p = Policy.Handle<Exception>().RetryAsync(2); p.ExecuteAsync(() => Task.CompletedTask);")]
    [InlineData("IAsyncPolicy p = Policy.Handle<Exception>().RetryAsync(2); p.ExecuteAsync(() => Task.CompletedTask);")]
    [InlineData("Policy<int>.Handle<Exception>().Retry(2).Execute(() => 1);")]
    [InlineData("Policy<int>.Handle<Exception>().RetryAsync(2).ExecuteAsync(() => Task.FromResult(1));")]
    [InlineData("Policy.Handle<Exception>().Retry(2).Execute(_ => { }, new Context());")]
    [InlineData("Policy.Handle<Exception>().RetryAsync(2).ExecuteAsync(_ => Task.CompletedTask, CancellationToken.None);")]
    public void Unkeyed_calls_take_no_fate_from_the_records(string source)
    {
        var match = Find(source, [RealPolly.Value]);

        Assert.Null(match);
    }

    [Theory]
    [InlineData("6.0.0.0")]
    [InlineData("8.0.0.0")]
    public void Polly_outside_its_range_takes_no_record_and_is_reported(string version)
    {
        var references = new[] { OutOfRangePolly(version) };
        var match = Find("var p = new RetryPolicy(); p.Execute(() => { });", references, out var compilation);

        Assert.Equal(LibraryMatchKind.OutOfRange, match?.Kind);
        var reference = Assert.Single(LibraryModels.BuiltIn.OutOfRangeReferences(compilation));
        Assert.Equal((POLLY, new Version(version)), (reference.Assembly.Name, reference.Assembly.Version));
        Assert.Equal((new Version(7, 0, 0, 0), new Version(8, 0, 0, 0)), (reference.Range.Minimum, reference.Range.MaximumExclusive));
    }

    [Fact]
    public void Polly_in_its_range_is_not_reported()
    {
        Find("var p = Policy.Handle<Exception>().Retry(2); p.Execute(() => { });", [RealPolly.Value], out var compilation);

        Assert.DoesNotContain(LibraryModels.BuiltIn.OutOfRangeReferences(compilation), reference => reference.Assembly.Name == POLLY);
    }

    /// <summary>What the built-in models say about the last statement of a method body compiled against the given references: the
    /// call in it, found with its receiver's static type.</summary>
    /// <param name="statements">The statements of the method body; the last is the call asked about.</param>
    /// <param name="polly">The <c>Polly</c> assembly the body is compiled against.</param>
    private static LibraryMatch? Find(string statements, IEnumerable<MetadataReference> polly) => Find(statements, polly, out _);

    /// <summary><see cref="Find(string, IEnumerable{MetadataReference})"/>, with the compilation it asked.</summary>
    /// <param name="statements">The statements of the method body; the last is the call asked about.</param>
    /// <param name="polly">The <c>Polly</c> assembly the body is compiled against.</param>
    /// <param name="compilation">The compilation the body is in.</param>
    private static LibraryMatch? Find(string statements, IEnumerable<MetadataReference> polly, out CSharpCompilation compilation)
    {
        var tree = CSharpSyntaxTree.ParseText($"{PRELUDE} class C {{ void M() {{ {statements} }} }}");
        compilation = CSharpCompilation.Create("Consumer", [tree], StubAssemblies.PlatformWithout([]).Concat(polly),
                                               new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));

        var model = compilation.GetSemanticModel(tree);
        var body = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single().Body!;
        var call = (InvocationExpressionSyntax)Assert.IsType<ExpressionStatementSyntax>(body.Statements.Last()).Expression;
        var method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(call).Symbol);
        var receiver = model.GetTypeInfo(((MemberAccessExpressionSyntax)call.Expression).Expression).Type;
        return LibraryModels.BuiltIn.Find(method, compilation, receiver);
    }

    /// <summary>An assembly named <c>Polly</c> at the given version, with the two types the records name and the one member they describe.</summary>
    /// <param name="version">The assembly version, four parts.</param>
    private static MetadataReference OutOfRangePolly(string version)
    {
        var library = CSharpCompilation.Create(POLLY, [CSharpSyntaxTree.ParseText($$"""
            [assembly: System.Reflection.AssemblyVersion("{{version}}")]
            namespace Polly { public abstract class Policy { public void Execute(System.Action action) { } } }
            namespace Polly.Retry { public class RetryPolicy : Polly.Policy { } }
            """)], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        var emitted = library.Emit(bytes);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return MetadataReference.CreateFromImage(bytes.ToArray());
    }
}
