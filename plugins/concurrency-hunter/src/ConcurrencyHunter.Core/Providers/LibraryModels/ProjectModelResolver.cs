using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels;

internal static class ProjectModelResolver
{
    internal static (LibraryModels Models, IReadOnlyList<ModelRejection> Rejections) Resolve(
        ProjectModelFiles files, IReadOnlyList<Compilation> compilations, ModelLock modelLock)
    {
        var rejections = new List<ModelRejection>(files.Rejections);
        var accepted = new List<(ProjectModelEntry Entry, string Id, SupportedAssemblyVersion Range, Version ResolvedVersion)>();
        var referenced = compilations.SelectMany(compilation => compilation.SourceModule.ReferencedAssemblySymbols.Append(compilation.Assembly))
                                     .DistinctBy(assembly => (assembly.Identity.Name, assembly.Identity.Version)).ToArray();
        foreach (var entry in files.Entries)
        {
            var named = referenced.Where(assembly => entry.Assemblies.Contains(assembly.Identity.Name, StringComparer.Ordinal)).ToArray();
            if (named.Length == 0)
                continue;
            if (ProjectModelFiles.IsPattern(entry.Member))
            {
                foreach (var assembly in named)
                {
                    var compilation = compilations.First(candidate => candidate.SourceModule.ReferencedAssemblySymbols
                        .Append(candidate.Assembly).Any(symbol => SymbolEqualityComparer.Default.Equals(symbol, assembly)));
                    var ids = modelLock.Members(entry.Member, assembly, compilation);
                    if (ids.Count == 0)
                    {
                        rejections.Add(Rejected(entry, $"pattern names no member in {assembly.Identity.Name} {assembly.Identity.Version}."));
                        continue;
                    }
                    foreach (var id in ids)
                    {
                        var resolvedMethod = DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation)
                                                           .OfType<IMethodSymbol>()
                                                           .FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(
                                                               candidate.ContainingAssembly, assembly));
                        if (resolvedMethod is null)
                        {
                            rejections.Add(new ModelRejection(entry.Path, id, "locked member does not exist in this assembly version."));
                            continue;
                        }
                        var rejectionReason = RejectReason(entry, resolvedMethod);
                        if (rejectionReason is not null)
                        {
                            rejections.Add(new ModelRejection(entry.Path, id, rejectionReason));
                            continue;
                        }
                        accepted.Add((entry, DocumentationCommentId.CreateDeclarationId(resolvedMethod.OriginalDefinition)!,
                                      Range(entry, assembly), assembly.Identity.Version));
                    }
                }
                continue;
            }
            var inRange = named.Where(assembly => entry.Versions is not { } range || range.Minimum <= assembly.Identity.Version &&
                                                                            assembly.Identity.Version < range.Maximum).ToArray();
            if (inRange.Length == 0)
            {
                rejections.Add(Rejected(entry, "no referenced assembly version is inside the entry's versions."));
                continue;
            }

            IMethodSymbol[] methods;
            try
            {
                methods = compilations.SelectMany(compilation => DocumentationCommentId.GetSymbolsForDeclarationId(entry.Member, compilation))
                                      .OfType<IMethodSymbol>()
                                      .Select(method => (method.ReducedFrom ?? method).OriginalDefinition)
                                      .Where(method => inRange.Any(assembly =>
                                          SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, assembly)))
                                      .DistinctBy(method => (method.ContainingAssembly.Identity.Name,
                                                              method.ContainingAssembly.Identity.Version,
                                                              DocumentationCommentId.CreateDeclarationId(method))).ToArray();
            }
            catch (ArgumentException)
            {
                rejections.Add(Rejected(entry, "member is not a readable declaration id."));
                continue;
            }
            if (methods.Length == 0)
            {
                rejections.Add(Rejected(entry, "member does not exist in a named referenced assembly and version."));
                continue;
            }
            foreach (var resolved in methods)
            {
                var reason = RejectReason(entry, resolved);
                if (reason is not null)
                {
                    rejections.Add(Rejected(entry, $"{resolved.ContainingAssembly.Identity.Name} " +
                                                  $"{resolved.ContainingAssembly.Identity.Version}: {reason}"));
                    continue;
                }
                accepted.Add((entry, DocumentationCommentId.CreateDeclarationId(resolved)!,
                              Range(entry, resolved.ContainingAssembly), resolved.ContainingAssembly.Identity.Version));
            }
        }

        var models = new List<LibraryModel>();
        foreach (var group in accepted.GroupBy(item => (item.Id, item.Range.AssemblyName, item.ResolvedVersion)))
        {
            var first = group.First().Entry;
            var conflict = group.Any(item => !Alike(first, item.Entry));
            if (conflict)
            {
                foreach (var item in group.DistinctBy(item => (item.Entry.Path, item.Entry.Position)))
                    rejections.Add(Rejected(item.Entry, "project entries disagree about this member; it is opaque."));
            }
            models.Add(new LibraryModel(group.Key.Id, group.Select(item => item.Range).Distinct().ToArray(),
                                        conflict ? [] : first.Effects, ModelLayer.Project, conflict || first.Opaque,
                                        group.Key.ResolvedVersion));
        }
        return (models.Count == 0 ? LibraryModels.BuiltIn : LibraryModels.WithProject(models), rejections);
    }

    private static bool Alike(ProjectModelEntry a, ProjectModelEntry b) => a.Opaque == b.Opaque &&
        (a.Opaque || a.Effects.ToHashSet().SetEquals(b.Effects));

    private static SupportedAssemblyVersion Range(ProjectModelEntry entry, IAssemblySymbol assembly)
    {
        var version = entry.Versions ?? (new Version(0, 0, 0, 0), new Version(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue));
        return new SupportedAssemblyVersion(assembly.Identity.Name, version.Item1, version.Item2);
    }

    private static string? RejectReason(ProjectModelEntry entry, IMethodSymbol method)
    {
        var typeName = method.ContainingType.ContainingNamespace.ToDisplayString() + "." + method.ContainingType.MetadataName;
        return entry.Versions is { } range && (method.ContainingAssembly.Identity.Version < range.Minimum ||
                                                method.ContainingAssembly.Identity.Version >= range.Maximum) ?
                   "assembly version is outside the entry's versions." :
               method.MethodKind == MethodKind.PropertySet ? "setters are not supported." :
               method.Parameters.Any(parameter => parameter.Type.TypeKind == TypeKind.Delegate) ? "delegate-typed parameters are not supported." :
               LibraryModels.IsRecognizedType(typeName) ? "a phase 3-4 recognizer owns this type." :
               method.DeclaringSyntaxReferences.Length != 0 ? "member has a body in the run." :
               entry.Effects.Any(effect => method.Parameters.All(parameter => parameter.Name != effect.Parameter)) ?
                   "an effect names a missing parameter." : null;
    }

    private static ModelRejection Rejected(ProjectModelEntry entry, string reason) =>
        new(entry.Path, entry.Member, reason);
}
