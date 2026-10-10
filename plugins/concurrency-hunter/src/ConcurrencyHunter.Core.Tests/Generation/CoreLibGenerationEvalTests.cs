using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>CoreLib generation against the installed .NET 8 implementation, only under <c>CH_MODEL_EVALS=1</c>.</summary>
[Collection(ModelEvalTests.COLLECTION)]
public sealed class CoreLibGenerationEvalTests
{
    private const string CORELIB = "System.Private.CoreLib";
    private static readonly Lazy<LibraryCompilationResult> Library = new(Compile);
    private static readonly string[] Candidates =
    [
        "M:System.ApplicationId.Equals(System.Object)",
        "M:System.Collections.ArrayList.get_IsSynchronized",
        "M:System.ComponentModel.DefaultValueAttribute.Equals(System.Object)",
        "M:System.Globalization.GregorianCalendar.GetDaysInYear(System.Int32,System.Int32)",
        "M:System.Globalization.GregorianCalendar.set_TwoDigitYearMax(System.Int32)",
        "M:System.Reflection.ExceptionHandlingClause.get_TryLength",
        "M:System.ResolveEventArgs.#ctor(System.String,System.Reflection.Assembly)",
        "M:System.Threading.CancellationToken.ThrowIfCancellationRequested",
        "M:System.Threading.CancellationTokenRegistration.op_Inequality(System.Threading.CancellationTokenRegistration,System.Threading.CancellationTokenRegistration)",
        "M:System.TupleExtensions.Deconstruct``11(System.Tuple{``0,``1,``2,``3,``4,``5,``6,System.Tuple{``7,``8,``9,``10}},``0@,``1@,``2@,``3@,``4@,``5@,``6@,``7@,``8@,``9@,``10@)",
        "M:System.Tuple`8.GetHashCode"
    ];
    private static readonly Lazy<GenerationTrace[]> Runs = new(() => Candidates.Select(Trace).ToArray());

    [RequiresModelEvalsFact]
    public void Claimed_CoreLib_member_names_its_recognizer()
    {
        var answer = Trace("M:System.Threading.Tasks.TaskFactory.StartNew(System.Action{System.Object},System.Object)").Answer;

        Assert.Equal(GenerationReasons.ENGINE_RECOGNIZED, answer.Reason);
        Assert.Contains("spawn", answer.Detail);
    }

    [RequiresModelEvalsFact]
    public void Unclaimed_members_are_classified_within_the_closure_bound()
    {
        Assert.Equal(Candidates.Length, Runs.Value.Length);
        foreach (var trace in Runs.Value)
        {
            var answer = trace.Answer;
            Assert.True(answer.Reason is null, $"{answer.Member}: {answer.Reason}: {answer.Detail}");
            Assert.NotNull(answer.Classified);
            Assert.NotNull(trace.Run);
            Assert.InRange(answer.Generation.ReachedBodies, 1, 1500);
        }
    }

    [RequiresModelEvalsFact]
    public void No_generation_reaches_a_System_SR_body()
    {
        var program = ProgramIndexBuilder.Build("corelib-sr", [Library.Value.Compilation!], Path.GetTempPath(), CancellationToken.None);
        var sr = Assert.Single(program.Types, type => type.Assembly == CORELIB && type.DisplayName == "System.SR");
        var bodies = program.MethodsOf(sr.TypeKey).Where(method => method.HasSourceBody)
                            .SelectMany(method => method.NestedBodyIds.Prepend(method.MethodId)).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(bodies);
        foreach (var trace in Runs.Value)
        {
            Assert.NotNull(trace.Run);
            Assert.DoesNotContain(trace.Run.Reachable.ReachedBodies.Keys,
                                  body => bodies.Contains(body));
        }
    }

    [RequiresModelEvalsFact]
    public void Every_ConstructorInvoker_Invoke_overload_returns_an_answer()
    {
        var type = Library.Value.Compilation!.GetTypeByMetadataName("System.Reflection.ConstructorInvoker");
        Assert.NotNull(type);
        var methods = type.GetMembers("Invoke").OfType<IMethodSymbol>().ToArray();
        Assert.NotEmpty(methods);
        foreach (var method in methods)
        {
            var id = method.GetDocumentationCommentId()!;
            Assert.Equal(id, Trace(id).Answer.Member);
        }
    }

    [RequiresModelEvalsFact]
    public void Every_CoreLib_body_lowers_without_an_escaping_exception()
    {
        var compilation = Library.Value.Compilation!;
        var program = ProgramIndexBuilder.Build("corelib-lowering", [compilation], Path.GetTempPath(), CancellationToken.None);
        var bodies = program.Methods.Where(method => method.HasSourceBody).Select(method => method.MethodId).ToHashSet(StringComparer.Ordinal);
        var methods = Types(compilation.Assembly.GlobalNamespace).SelectMany(type => type.GetMembers().OfType<IMethodSymbol>())
                           .Where(method => bodies.Contains(IrLowering.RootBodyId(method))).ToArray();
        Assert.NotEmpty(methods);
        foreach (var method in methods)
        {
            var error = Record.Exception(() => IrLowering.Lower(method, compilation, Path.GetTempPath(), CancellationToken.None));
            Assert.True(error is null, $"{method.GetDocumentationCommentId()}: {error}");
        }
    }

    private static GenerationTrace Trace(string member) =>
        ModelGenerator.Trace(new GenerationRequest(CORELIB, "8.0", member, null, null), Library.Value, CancellationToken.None);

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol space) =>
        space.GetTypeMembers().SelectMany(Nested).Concat(space.GetNamespaceMembers().SelectMany(Types));

    private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) =>
        type.GetTypeMembers().SelectMany(Nested).Prepend(type);

    private static LibraryCompilationResult Compile()
    {
        var shared = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), ".."));
        var directory = Directory.GetDirectories(shared)
                                 .Select(path => (Path: path, Version: Version.TryParse(Path.GetFileName(path), out var version) ? version : null))
                                 .Where(item => item.Version is { Major: 8, Minor: 0 })
                                 .OrderByDescending(item => item.Version).FirstOrDefault().Path;
        Assert.True(directory is not null, "The .NET 8 shared framework is not installed.");
        var path = Path.Combine(directory, CORELIB + ".dll");
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var assembly = new ImplementationAssembly(CORELIB, path, null, Path.GetFileName(directory), "net8.0", directory, [], [],
                                                  AssemblyName.GetAssemblyName(path).Version!.ToString(), FileVersionInfo.GetVersionInfo(path).FileVersion!,
                                                  metadata.GetGuid(metadata.GetModuleDefinition().Mvid));
        var library = LibraryCompilation.Compile(assembly, CancellationToken.None);
        Assert.True(library.Reason is null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        Assert.NotNull(library.Compilation);
        Assert.Equal(CORELIB, library.Compilation.AssemblyName);
        Assert.True(library.Bodies > 0);
        return library;
    }
}
