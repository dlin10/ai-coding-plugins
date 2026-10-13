using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Providers.LibraryModels;
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
        "M:System.Collections.Generic.GenericEqualityComparer`1.GetHashCode",
        "M:System.ComponentModel.EditorBrowsableAttribute.Equals(System.Object)",
        "M:System.Globalization.NumberFormatInfo.get_CurrencyDecimalDigits",
        "M:System.Globalization.NumberFormatInfo.set_PercentSymbol(System.String)",
        "M:System.ResolveEventArgs.#ctor(System.String,System.Reflection.Assembly)",
        "M:System.Text.StringBuilder.get_Capacity",
        "M:System.Text.StringBuilder.get_Chars(System.Int32)",
        "M:System.Threading.CancellationToken.ThrowIfCancellationRequested",
        "M:System.Threading.CancellationTokenRegistration.op_Inequality(System.Threading.CancellationTokenRegistration,System.Threading.CancellationTokenRegistration)",
        "M:System.TupleExtensions.Deconstruct``11(System.Tuple{``0,``1,``2,``3,``4,``5,``6,System.Tuple{``7,``8,``9,``10}},``0@,``1@,``2@,``3@,``4@,``5@,``6@,``7@,``8@,``9@,``10@)"
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

    /// <summary>A struct constructor that stores into its own fields writes the storage the caller constructs into, the struct receiver's
    /// own, so it answers <c>vocabulary</c> — never a model that leaves the store out, nor one whose argument keeps itself (R7).</summary>
    [RequiresModelEvalsFact]
    public void Every_struct_constructor_storing_into_its_own_fields_answers_vocabulary()
    {
        string[] members =
        [
            "M:System.ValueTuple`1.#ctor(`0)",
            "M:System.Runtime.InteropServices.ArrayWithOffset.#ctor(System.Object,System.Int32)",
            "M:System.Runtime.Serialization.StreamingContext.#ctor(System.Runtime.Serialization.StreamingContextStates,System.Object)",
            "M:System.Reflection.CustomAttributeNamedArgument.#ctor(System.Reflection.MemberInfo,System.Reflection.CustomAttributeTypedArgument)",
            "M:System.ValueTuple`8.#ctor(`0,`1,`2,`3,`4,`5,`6,`7)",
            "M:System.Reflection.CustomAttributeTypedArgument.#ctor(System.Type,System.Object)",
            "M:System.Text.StringBuilder.AppendInterpolatedStringHandler.#ctor(System.Int32,System.Int32,System.Text.StringBuilder)"
        ];

        foreach (var member in members)
        {
            var answer = Trace(member).Answer;
            Assert.True(answer.Model is null && answer.ModelReason == ModelReasons.VOCABULARY,
                        $"{member}: {answer.Reason} / {answer.ModelReason}: {answer.Detail}");
            Assert.Contains(answer.Causes, cause => cause.Code == ModelCauses.STRUCT_STORAGE_WRITE);
        }
    }

    /// <summary>A <c>CompareTo(object)</c> of a CoreLib struct that unboxes its argument through a pattern reads the user's object, the box's
    /// whole storage, as its step-10 answer did (R2).</summary>
    [RequiresModelEvalsFact]
    public void Every_struct_CompareTo_object_unboxing_its_argument_reads_it()
    {
        (string Member, string Parameter)[] members =
        [
            ("M:System.DateOnly.CompareTo(System.Object)", "value"),
            ("M:System.Guid.CompareTo(System.Object)", "value"),
            ("M:System.Int128.CompareTo(System.Object)", "value"),
            ("M:System.UInt128.CompareTo(System.Object)", "value"),
            ("M:System.TimeOnly.CompareTo(System.Object)", "value"),
            ("M:System.Runtime.InteropServices.NFloat.CompareTo(System.Object)", "obj")
        ];

        foreach (var (member, parameter) in members)
        {
            var answer = Trace(member).Answer;
            Assert.True(answer.Model is not null, $"{member}: {answer.Reason} / {answer.ModelReason}: {answer.Detail}");
            Assert.Equal([LibraryEffect.DeepReadOf(parameter)], answer.Model.Effects);
        }
    }

    /// <summary>The Nullable comparers that call the default comparer of T, which CoreLib builds by reflection through an extern, make a
    /// call on a receiver the analysis cannot follow, so each is refused rather than given a model that leaves the user's comparison out.
    /// <c>NullableEqualityComparer&lt;T&gt;.GetHashCode(T?)</c> calls <c>obj.GetHashCode()</c> and no default comparer, so it is not
    /// among them.</summary>
    [RequiresModelEvalsFact]
    public void Every_Nullable_comparer_calling_the_default_comparer_is_refused()
    {
        string[] members =
        [
            "M:System.Collections.Generic.NullableComparer`1.Compare(System.Nullable{`0},System.Nullable{`0})",
            "M:System.Collections.Generic.NullableEqualityComparer`1.Equals(System.Nullable{`0},System.Nullable{`0})"
        ];

        foreach (var member in members)
        {
            var answer = Trace(member).Answer;
            Assert.True(answer.Model is null && answer.ModelReason == ModelReasons.UNKNOWN_TOUCH,
                        $"{member}: {answer.Reason} / {answer.ModelReason}: {answer.Detail}");
            Assert.Contains(answer.Causes, cause => cause.Code == ModelCauses.CALL_RECEIVER_UNFOLLOWED);
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
