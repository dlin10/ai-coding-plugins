using System.Diagnostics;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Analysis;

internal sealed record CallSiteTarget(CallIdentity Call, string Callee, IMethodSymbol? Member, string? Origin, bool Dispatches,
                                       string? Outcome, Compilation? Library);

internal sealed record CallSiteLibrary(string? Origin, GenerationTargetAnswer Answer, LibraryCompilationResult? Compiled);

/// <summary>Classifies the calls deciding the latest pairs, independently of generation eligibility.</summary>
/// <param name="compilations">Owns library compilations for this analyzer invocation.</param>
/// <param name="generation">The implementation resolver and source provider.</param>
/// <param name="cancellationToken">Stops library resolution and compilation.</param>
/// <param name="libraryLookup">The library classification and compilation seam used by small fixture libraries.</param>
internal sealed class DecidingCalls(CallSiteCompilations compilations, GenerationSetup generation, CancellationToken cancellationToken,
                                      Func<IMethodSymbol?, Compilation, CallSiteLibrary>? libraryLookup = null)
{
    private readonly ImplementationAssemblies _install = generation.Install ?? ImplementationAssemblies.ForThisProcess();

    internal IReadOnlyList<CallSiteTarget> Of(ScopeRun run, PairAnalysis pairs, IReadOnlyList<Compilation> projects) =>
        pairs.Pairs.SelectMany(pair => pair.DecidingCalls).Select(decision => decision.Call).OfType<CallIdentity>().Distinct()
             .OrderBy(call => call.BodyId, StringComparer.Ordinal).ThenBy(call => call.Span.Path, StringComparer.Ordinal)
             .ThenBy(call => call.Span.StartLine).ThenBy(call => call.Span.StartColumn)
             .ThenBy(call => call.Target, StringComparer.Ordinal).ThenBy(call => call.Ordinal)
             .Select(call => Target(run, call, projects)).Where(target => target.Origin is not null).ToArray();

    internal CallSiteTarget Target(ScopeRun run, CallIdentity call, IReadOnlyList<Compilation> projects)
    {
        var project = projects.FirstOrDefault(project => call.BodyId.StartsWith($"body:{project.AssemblyName}:", StringComparison.Ordinal));
        if (project is null || !run.Reachable.Bodies.TryGetValue(call.BodyId, out var body))
            return new(call, call.Target, null, null, false, null, null);
        var operationId = body.CallIdentities.FirstOrDefault(pair => pair.Value == call).Key;
        var operation = body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>().First(operation => operation.Id == operationId);
        var dispatches = operation.CallKind is IrCallKind.Virtual or IrCallKind.Interface or IrCallKind.Delegate;
        var member = CallTargets.Resolve(project, call.Target);
        var supplied = libraryLookup?.Invoke(member, project);
        var assembly = member?.ContainingAssembly ?? project.References.Select(reference => project.GetAssemblyOrModuleSymbol(reference))
            .OfType<IAssemblySymbol>().FirstOrDefault(assembly => assembly.Name == CallTargets.Split(call.Target)?.AssemblyName);
        var origin = supplied?.Origin ?? (assembly is null ? null : GenerationTargets.LibraryOrigin(assembly, project, _install));
        if (member is null || origin is null)
            return new(call, operation.Method, member, origin, dispatches, origin is null ? null : origin == "framework" ? "framework" : "no-body", null);
        if (origin == "framework")
            return new(call, operation.Method, member, origin, dispatches, "framework", null);
        var answer = supplied?.Answer ?? GenerationTargets.Of(member, project, _install, cancellationToken);
        var outcome = AnswerOf(answer);
        if (dispatches || outcome is not null)
            return new(call, operation.Method, member, origin, dispatches, dispatches ? "no-body" : outcome, null);
        var compiled = supplied?.Compiled ?? compilations.ForTarget((GenerationTarget)answer, generation, cancellationToken);
        var declaration = CallTargets.Split(call.Target)!.Value.Id;
        var library = compiled?.Compilation;
        var executable = library is not null && !compiled!.ExternMembers.Contains(declaration) &&
                         DocumentationCommentId.GetFirstSymbolForDeclarationId(declaration, library) is IMethodSymbol { IsAbstract: false, IsExtern: false } target &&
                         ProgramIndexBuilder.Build("call-site", [library], string.Empty, cancellationToken).Method(call.Target) is { HasSourceBody: true };
        return new(call, operation.Method, member, origin, dispatches, executable ? null : "no-body", library);
    }

    // bounded: forwards once to the nonrecursive reason overload.
    internal static string? AnswerOf(GenerationTargetAnswer answer) => answer switch
    {
        GenerationTarget => null,
        UnavailableGenerationTarget => "no-body",
        NoGenerationTarget target => AnswerOf(target.Reason),
        _ => throw new UnreachableException($"Unknown generation target answer {answer}.")
    };

    internal static string? AnswerOf(NoTargetReason reason) => reason switch
    {
        NoTargetReason.Source or NoTargetReason.OtherPath => "neither",
        NoTargetReason.FunctionPointer or NoTargetReason.AmbiguousId or NoTargetReason.NoFrameworkPack or NoTargetReason.Forwarded => "no-body",
        NoTargetReason.CoreLib => "framework",
        NoTargetReason.Recognized => "recognized",
        _ => throw new UnreachableException($"Unknown generation target reason {reason}.")
    };
}
