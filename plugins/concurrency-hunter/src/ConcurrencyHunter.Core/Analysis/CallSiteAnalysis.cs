using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Frontend;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ConcurrencyHunter.Analysis;

internal sealed record CallSiteMeasurement(string ScopeId, int LibraryBodies, IReadOnlyList<string> Admitted);

internal sealed record CallSiteAnalysis(string AssemblyName, Version Minimum, Version MaximumExclusive, IReadOnlySet<string> WithheldFamilies,
                                        bool AdmitBodies = true)
{
    internal List<CallSiteMeasurement> Measurements { get; } = [];

    internal bool Matches(IMethodSymbol method) => method.ContainingAssembly.Name == AssemblyName &&
        method.ContainingAssembly.Identity.Version >= Minimum && method.ContainingAssembly.Identity.Version < MaximumExclusive;
}

/// <summary>The library compilations owned by one call-site probe.</summary>
internal sealed class CallSiteCompilations : IDisposable
{
    private readonly CompilationOwnership _ownership = new();
    private readonly Dictionary<string, LibraryCompilationResult?> _compiled = new(StringComparer.Ordinal);

    internal (IReadOnlyList<Compilation> Libraries, ReachabilityRules Rules) ForScope(IReadOnlyList<Compilation> projects,
                                                                                   CallSiteAnalysis? setup, GenerationSetup generation,
                                                                                   CancellationToken cancellationToken)
    {
        if (setup is not { AdmitBodies: true })
            return ([], ReachabilityRules.ClassHierarchy);
        var install = generation.Install ?? ImplementationAssemblies.ForThisProcess();
        foreach (var project in projects)
        {
            WorkCancellation.Check();
            foreach (var tree in project.SyntaxTrees)
            {
                WorkCancellation.Check();
                cancellationToken.ThrowIfCancellationRequested();
                var model = project.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes().Where(node =>
                    node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or MemberAccessExpressionSyntax))
                {
                    WorkCancellation.Check();
                    var symbol = model.GetSymbolInfo(node, cancellationToken).Symbol;
                    var method = symbol as IMethodSymbol ?? (symbol as IPropertySymbol)?.GetMethod;
                    if (method is null || !setup.Matches(method))
                        continue;
                    if (!_compiled.ContainsKey(setup.AssemblyName))
                    {
                        using var owned = _ownership.Open();
                        LibraryCompilationResult? compilation = null;
                        if (GenerationTargets.Of(method, project, install, cancellationToken) is GenerationTarget target)
                        {
                            var request = target.Request;
                            var resolved = install.Resolve(request.AssemblyName, request.Version, request.PackageId, request.Framework, cancellationToken);
                            if (resolved is { Reason: null, Assembly: { } assembly })
                            {
                                var sources = generation.Sources?.GetAsync(assembly.Path, cancellationToken).GetAwaiter().GetResult();
                                compilation = LibraryCompilation.Compile(assembly, sources, new GenerationChecks(cancellationToken), () => { });
                            }
                        }
                        _compiled.Add(setup.AssemblyName, compilation);
                    }
                    if (_compiled[setup.AssemblyName] is not { Compilation: { } library } compiled)
                        return ([], ReachabilityRules.ClassHierarchy);
                    var index = ProgramIndexBuilder.Build("call-site", [library], "", cancellationToken);
                    var ids = index.Methods.Where(method => method.HasSourceBody && !method.IsAbstract &&
                        method.MethodId.StartsWith($"body:{setup.AssemblyName}:", StringComparison.Ordinal) &&
                        !compiled.ExternMembers.Contains(method.MethodId[($"body:{setup.AssemblyName}:").Length..]) &&
                        DocumentationCommentId.GetFirstSymbolForDeclarationId(method.MethodId[($"body:{setup.AssemblyName}:").Length..], library) is IMethodSymbol { IsExtern: false })
                                   .Select(method => method.MethodId).ToHashSet(StringComparer.Ordinal);
                    return ([library], ReachabilityRules.ClassHierarchy with { Admission = new CallSiteAdmission(setup.AssemblyName, ids) });
                }
            }
        }
        return ([], ReachabilityRules.ClassHierarchy);
    }

    public void Dispose() => LibraryCompilation.Release(_ownership.Created);
}
