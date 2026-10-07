using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests;

/// <summary>Which members of the task awaiters and configured awaitables the lowering recognizes, and that the generator's claims agree
/// (R3): <c>GetResult()</c> of the eight awaiters and <c>GetAwaiter()</c> of the four configured awaitables are recognized and claimed;
/// <c>OnCompleted</c> and <c>UnsafeOnCompleted</c> are neither, so the callback they are handed stays an unresolved handoff.</summary>
public sealed class AwaiterRecognitionTests
{
    /// <summary>The line of a fixture method holding the call a row reads.</summary>
    private const string MARK = "/*T*/";

    private static readonly Lazy<Compilation> Fixture = new(Compile, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>A type whose members a row reads.</summary>
    public enum Owner
    {
        TaskAwaiter,
        TaskAwaiterOfT,
        ValueTaskAwaiter,
        ValueTaskAwaiterOfT,
        ConfiguredTaskAwaiter,
        ConfiguredTaskAwaiterOfT,
        ConfiguredValueTaskAwaiter,
        ConfiguredValueTaskAwaiterOfT,
        ConfiguredTaskAwaitable,
        ConfiguredTaskAwaitableOfT,
        ConfiguredValueTaskAwaitable,
        ConfiguredValueTaskAwaitableOfT
    }

    /// <summary>Who reads the member.</summary>
    public enum Reader
    {
        Lowering,
        EngineClaims
    }

    /// <summary>Every row: the awaiters' <c>GetResult</c>, <c>OnCompleted</c> and <c>UnsafeOnCompleted</c> and the configured awaitables'
    /// <c>GetAwaiter</c>, each read by the lowering and by the generator's claims.</summary>
    public static TheoryData<Owner, string, Reader> Rows()
    {
        var rows = new TheoryData<Owner, string, Reader>();
        foreach (var owner in Enum.GetValues<Owner>())
        foreach (var member in MembersOf(owner))
        foreach (var reader in Enum.GetValues<Reader>())
            rows.Add(owner, member, reader);
        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Member_is_recognized_and_claimed_exactly_when_it_is_get_result_or_get_awaiter(Owner owner, string member, Reader reader)
    {
        var (method, call) = Read(owner, member);
        Assert.Equal(TypeOf(owner), method.ContainingType.OriginalDefinition.ToDisplayString());
        Assert.Equal(member, method.Name);
        var expected = member is "GetResult" or "GetAwaiter";
        var actual = reader == Reader.Lowering ? call.IsRecognized : EngineClaims.Of(method) == "recognized";
        Assert.Equal(expected, actual);
        if (reader == Reader.EngineClaims && !expected)
            Assert.Null(EngineClaims.Of(method));
    }

    [Fact]
    public void Callback_handed_to_a_task_awaiter_s_on_completed_runs_in_an_unknown_execution()
    {
        var run = Analyze("""
            public static class Opaque
            {
                public static extern void Hand(Action callback);
            }

            public sealed class Worker : BackgroundService
            {
                private readonly Task<int> _task = Task.FromResult(1);
                public int Value;
                public void F() => Value = 1;
                public void G() => Value = 2;

                protected override Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var awaiter = _task.GetAwaiter();
                    awaiter.OnCompleted(() => F());
                    Opaque.Hand(() => G());
                    return Task.CompletedTask;
                }
            }
            """ + Startup("services.AddHostedService<Worker>();"));

        // The callback of OnCompleted runs where the callback of any unresolved call does: in an unknown execution.
        foreach (var helper in new[] { "F", "G" })
        {
            var writes = run.Accesses("Value").Where(access => access.Symbol.EndsWith($".{helper}()", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(writes);
            Assert.All(writes, access => Assert.Equal("unknown-delegate-call", access.Root.RootKind));
        }
    }

    [Fact]
    public void The_table_has_twenty_eight_members_read_twice()
    {
        Assert.Equal(28, Enum.GetValues<Owner>().Sum(owner => MembersOf(owner).Length));
        Assert.Equal(56, Rows().Cast<object>().Count());
    }

    // ---- the fixture ----

    private static string[] MembersOf(Owner owner) =>
        owner >= Owner.ConfiguredTaskAwaitable ? ["GetAwaiter"] : ["GetResult", "OnCompleted", "UnsafeOnCompleted"];

    /// <summary>The display name of the original definition of a row's type.</summary>
    /// <param name="owner">The row's type.</param>
    private static string TypeOf(Owner owner) => owner switch
    {
        Owner.TaskAwaiter => "System.Runtime.CompilerServices.TaskAwaiter",
        Owner.TaskAwaiterOfT => "System.Runtime.CompilerServices.TaskAwaiter<TResult>",
        Owner.ValueTaskAwaiter => "System.Runtime.CompilerServices.ValueTaskAwaiter",
        Owner.ValueTaskAwaiterOfT => "System.Runtime.CompilerServices.ValueTaskAwaiter<TResult>",
        Owner.ConfiguredTaskAwaiter => "System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter",
        Owner.ConfiguredTaskAwaiterOfT => "System.Runtime.CompilerServices.ConfiguredTaskAwaitable<TResult>.ConfiguredTaskAwaiter",
        Owner.ConfiguredValueTaskAwaiter => "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter",
        Owner.ConfiguredValueTaskAwaiterOfT => "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<TResult>.ConfiguredValueTaskAwaiter",
        Owner.ConfiguredTaskAwaitable => "System.Runtime.CompilerServices.ConfiguredTaskAwaitable",
        Owner.ConfiguredTaskAwaitableOfT => "System.Runtime.CompilerServices.ConfiguredTaskAwaitable<TResult>",
        Owner.ConfiguredValueTaskAwaitable => "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable",
        Owner.ConfiguredValueTaskAwaitableOfT => "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<TResult>",
        _ => throw new ArgumentOutOfRangeException(nameof(owner), owner, null)
    };

    /// <summary>The parameter a row's method takes and the receiver it makes of it.</summary>
    /// <param name="owner">The row's type.</param>
    private static (string Parameter, string Receiver) Setup(Owner owner) => owner switch
    {
        Owner.TaskAwaiter => ("Task t", "t.GetAwaiter()"),
        Owner.TaskAwaiterOfT => ("Task<int> t", "t.GetAwaiter()"),
        Owner.ValueTaskAwaiter => ("ValueTask t", "t.GetAwaiter()"),
        Owner.ValueTaskAwaiterOfT => ("ValueTask<int> t", "t.GetAwaiter()"),
        Owner.ConfiguredTaskAwaiter => ("Task t", "t.ConfigureAwait(false).GetAwaiter()"),
        Owner.ConfiguredTaskAwaiterOfT => ("Task<int> t", "t.ConfigureAwait(false).GetAwaiter()"),
        Owner.ConfiguredValueTaskAwaiter => ("ValueTask t", "t.ConfigureAwait(false).GetAwaiter()"),
        Owner.ConfiguredValueTaskAwaiterOfT => ("ValueTask<int> t", "t.ConfigureAwait(false).GetAwaiter()"),
        Owner.ConfiguredTaskAwaitable => ("Task t", "t.ConfigureAwait(false)"),
        Owner.ConfiguredTaskAwaitableOfT => ("Task<int> t", "t.ConfigureAwait(false)"),
        Owner.ConfiguredValueTaskAwaitable => ("ValueTask t", "t.ConfigureAwait(false)"),
        Owner.ConfiguredValueTaskAwaitableOfT => ("ValueTask<int> t", "t.ConfigureAwait(false)"),
        _ => throw new ArgumentOutOfRangeException(nameof(owner), owner, null)
    };

    private static string Invocation(string member) => member switch
    {
        "OnCompleted" or "UnsafeOnCompleted" => $"r.{member}(() => {{ }});",
        _ => $"r.{member}();"
    };

    private static Compilation Compile()
    {
        var methods = Enum.GetValues<Owner>()
                          .SelectMany(owner => MembersOf(owner).Select(member =>
                          {
                              var (parameter, receiver) = Setup(owner);
                              return $"    void {owner}_{member}({parameter})\n    {{\n        var r = {receiver};\n        {Invocation(member)} {MARK}\n    }}\n";
                          }));
        var source = "using System;\nusing System.Threading.Tasks;\n\nclass C\n{\n" + string.Concat(methods) + "}\n";
        var compilation = FixtureSolution.Create(("Case.cs", source)).Projects.Single().GetCompilationAsync().GetAwaiter().GetResult()!;
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return compilation;
    }

    /// <summary>The member a row's marked call calls and the call the lowering makes of it.</summary>
    /// <param name="owner">The row's type.</param>
    /// <param name="member">The row's member.</param>
    private static (IMethodSymbol Method, IrCallOperation Call) Read(Owner owner, string member)
    {
        var compilation = Fixture.Value;
        var declared = compilation.GetTypeByMetadataName("C")!.GetMembers($"{owner}_{member}").OfType<IMethodSymbol>().Single();
        var syntax = (MethodDeclarationSyntax)declared.DeclaringSyntaxReferences.Single().GetSyntax();
        var tree = syntax.SyntaxTree;
        var line = tree.GetText().Lines.Single(text => text.ToString().Contains(MARK, StringComparison.Ordinal) &&
                                                       syntax.Span.Contains(text.Span)).LineNumber + 1;
        var invocation = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
                               .Single(node => tree.GetLineSpan(node.Span).StartLinePosition.Line + 1 == line &&
                                               node.Parent is ExpressionStatementSyntax);
        var method = (IMethodSymbol)compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol!;
        var body = IrLowering.Lower(declared, compilation, @"C:\fixture", CancellationToken.None).Body;
        var call = body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>()
                       .Single(operation => operation.Provenance.Span.StartLine == line);
        return (method, call);
    }
}
