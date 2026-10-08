using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

/// <summary>The model generator run on a source fixture library, from the member check on.</summary>
internal static class GenerationRuns
{
    public const string ASSEMBLY = "Fixture.Library";

    /// <summary>How a matrix runs the generator over its cells: on at most half the cores. Without a bound the loop takes every thread
    /// the pool adds, and the classes running beside it, which wait on Roslyn's tasks, stall behind it for a minute at a time.</summary>
    public static readonly ParallelOptions CellParallelism = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };

    /// <summary>The types every fate fixture shares: a call the engine cannot follow, a holder of a delegate with a public trigger, a
    /// library static and a box with a delegate field.</summary>
    public const string PRELUDE = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Lib
        {
            public static class Sink { public static extern void Take(object o); }
            public sealed class Holder { private Action _a; public Holder(Action a) { _a = a; } public void Fire() { _a(); } }
            public static class Cache { public static object Last; }
            public sealed class Box { public Action A; }

        """;

    /// <summary>A source library compiled as the generator compiles a decompiled one: bodies in error made <c>extern</c>.</summary>
    /// <param name="source">The library's source.</param>
    /// <param name="assemblyName">The assembly name.</param>
    public static LibraryCompilationResult Compile(string source, string assemblyName = ASSEMBLY) =>
        LibraryCompilation.CompileTrees(assemblyName, [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), $"{assemblyName}/Library.cs")],
                                        EmittedAssemblies.RuntimeReferences, CancellationToken.None);

    /// <summary>The generator's trace for one member of a library of the prelude and <paramref name="types"/>.</summary>
    /// <param name="types">Declarations inside namespace <c>Lib</c>.</param>
    /// <param name="memberId">The member's declaration id.</param>
    public static GenerationTrace Trace(string types, string memberId) => TraceOf(PRELUDE + types + "\n}\n", memberId);

    /// <summary>The generator's trace for one member of a library of exactly <paramref name="source"/>.</summary>
    /// <param name="source">The library's source.</param>
    /// <param name="memberId">The member's declaration id.</param>
    /// <param name="assemblyName">The assembly name.</param>
    public static GenerationTrace TraceOf(string source, string memberId, string assemblyName = ASSEMBLY)
    {
        var library = Compile(source, assemblyName);
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        return ModelGenerator.Trace(new GenerationRequest(assemblyName, "1.0", memberId, null, null), library, CancellationToken.None);
    }

    /// <summary>The fate of one parameter, the answer being a classification.</summary>
    /// <param name="trace">The trace.</param>
    /// <param name="parameter">The parameter's name.</param>
    public static ClassifiedFate FateOf(GenerationTrace trace, string parameter)
    {
        var answer = trace.Answer;
        Assert.True(answer.Classified is not null, $"{answer.Reason}: {answer.Detail}");
        Assert.True(answer.Classified.TryGetValue(parameter, out var fate), $"{parameter} is not classified: {string.Join(", ", answer.Classified.Keys)}");
        return fate;
    }

    /// <summary>The executions a probe's fired field was written in.</summary>
    /// <param name="trace">The trace, with a run.</param>
    /// <param name="firedField">The probe's fired field.</param>
    public static IReadOnlyList<string> FiredIn(GenerationTrace trace, string firedField) =>
        trace.Run!.Executions!.Accesses.Where(access => access.Access.Field.Name == firedField).Select(access => access.ExecutionId)
             .Distinct().Order(StringComparer.Ordinal).ToArray();

    /// <summary>The regions a static slot of the driver's <c>Keep</c> or <c>ModelDriver</c> class points to.</summary>
    /// <param name="trace">The trace, with a run.</param>
    /// <param name="type"><c>Keep</c> or <c>ModelDriver</c>.</param>
    /// <param name="field">The field.</param>
    public static IReadOnlySet<string> Slot(GenerationTrace trace, string type, string field) =>
        trace.Run!.Heap!.PointsTo($"static:{DriverSynthesizer.ASSEMBLY}:{type}", HeapReachability.Slot(DriverSynthesizer.ASSEMBLY, type, field));

    /// <summary>The document id of the single ordinary method of a type with a name, in a compiled library.</summary>
    /// <param name="library">The library.</param>
    /// <param name="type">The type's metadata name.</param>
    /// <param name="name">The method's name.</param>
    public static string MethodId(LibraryCompilationResult library, string type, string name) =>
        library.Compilation!.GetTypeByMetadataName(type)!.GetMembers(name).OfType<IMethodSymbol>().Single().GetDocumentationCommentId()!;
}
