using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>Why a call's member is no generation target (R1, R6, R8): the run never generates it and does not list it in coverage.</summary>
internal enum NoTargetReason
{
    /// <summary>The member's signature carries a function-pointer type (<see cref="FunctionPointers"/>); no declaration id names it.</summary>
    FunctionPointer,

    /// <summary>The member has source in the scope or in the generated assembly's library copy.</summary>
    Source,

    /// <summary>The member's metadata reference is in neither a package's <c>lib</c> or <c>ref</c> folder under the NuGet packages root nor
    /// a shared framework's reference pack or, in a generator compilation, its implementation folder; another folder of a package,
    /// such as <c>runtimes/&lt;rid&gt;/lib</c>, is one too.</summary>
    OtherPath,

    /// <summary>A package member of a compilation that references no <c>Microsoft.NETCore.App</c> reference pack, so no target framework
    /// chooses its asset, and no generation context supplies one.</summary>
    NoFrameworkPack,

    /// <summary>A member of <c>System.Private.CoreLib</c>.</summary>
    CoreLib,

    /// <summary>The implementation forwards the member's declaring type to another assembly, <c>System.Private.CoreLib</c> or any other.</summary>
    Forwarded,

    /// <summary>The member's declaration id does not resolve to exactly this member of its assembly.</summary>
    AmbiguousId
}

/// <summary>What a call's member comes to as a generation target: <see cref="NoGenerationTarget"/>, <see cref="UnavailableGenerationTarget"/>
/// or <see cref="GenerationTarget"/>.</summary>
internal abstract record GenerationTargetAnswer;

/// <summary>A member the run never generates and does not list.</summary>
/// <param name="Reason">Why.</param>
internal sealed record NoGenerationTarget(NoTargetReason Reason) : GenerationTargetAnswer;

/// <summary>A member that is a generation target, with the two forms of its declaration id and the generated file that answers for it.</summary>
/// <param name="MemberId">The declaration id of the member's original definition as the generator writes it
/// (<see cref="ISymbol.GetDocumentationCommentId"/>): the id the files, ordering and coverage use.</param>
/// <param name="LookupId">The declaration id as <c>LibraryModels.Find</c> looks a call up
/// (<see cref="DocumentationCommentId.CreateDeclarationId"/>), which adds <c>~</c> and the return type to a non-void method.</param>
/// <param name="Key">The key of the generated file; a package member whose asset is not on disk leaves its MVID out.</param>
internal abstract record IdentifiedGenerationTarget(string MemberId, string LookupId, GeneratedFileKey Key) : GenerationTargetAnswer;

/// <summary>A target whose implementation is not on disk: the package version, the asset for the compilation's framework, the shared
/// framework of the major or the <c>Microsoft.AspNetCore.App</c> a package needs. A run lists it with <c>no-implementation</c>, keeps
/// nothing for it and tries again the next time.</summary>
/// <param name="MemberId">The declaration id the generator writes.</param>
/// <param name="LookupId">The declaration id a call is looked up by.</param>
/// <param name="Key">The part of the file key the reference names: a package's id and version, with the MVID of its asset when the asset is on
/// disk and the target is unavailable for a shared framework it needs, and without one when the version or the asset is missing; a framework
/// member's whole key.</param>
/// <param name="Detail">What is missing, in words.</param>
internal sealed record UnavailableGenerationTarget(string MemberId, string LookupId, GeneratedFileKey Key, string Detail)
    : IdentifiedGenerationTarget(MemberId, LookupId, Key);

/// <summary>A member the generator can be asked for.</summary>
/// <param name="MemberId">The declaration id the generator writes.</param>
/// <param name="LookupId">The declaration id a call is looked up by.</param>
/// <param name="Key">The key of the generated file: the whole key, with the MVID of the asset a package's framework selects.</param>
/// <param name="Request">The generator's request.</param>
internal sealed record GenerationTarget(string MemberId, string LookupId, GeneratedFileKey Key, GenerationRequest Request)
    : IdentifiedGenerationTarget(MemberId, LookupId, Key);

/// <summary>The one owner of which implementation and which generated file stand for a library member a call reaches (R1, R2, R6, R8).
/// It is kept per (compilation, declaring type) by whoever caches its answers, never per assembly: one reference assembly may declare one
/// type and forward another.</summary>
internal static class GenerationTargets
{
    private const string NETCORE_PACK = "Microsoft.NETCore.App.Ref";
    private const string ASPNETCORE_PACK = "Microsoft.AspNetCore.App.Ref";
    private const string NETCORE_FRAMEWORK = "Microsoft.NETCore.App";
    private const string ASPNETCORE_FRAMEWORK = "Microsoft.AspNetCore.App";

    /// <summary>What a call's member comes to. The checks that need no disk come first, the function-pointer rule before anything is
    /// resolved; then the implementation is found, and the member's declaring type is read from its exported types. A package member is
    /// requested with the compilation's target framework, the <c>net&lt;major&gt;.0</c> folder of its <c>Microsoft.NETCore.App.Ref</c>
    /// references, or the request's major in a generator compilation, so the resolver picks that framework's asset and not the host's.
    /// A generator compilation also recognizes the shared frameworks' implementation folders as framework origins.</summary>
    /// <param name="method">The method, constructor, operator or accessor a call names.</param>
    /// <param name="compilation">The compilation the call is in.</param>
    /// <param name="resolver">Finds the implementation assembly; its dotnet root and packages folder say where references come from.</param>
    /// <param name="cancellationToken">Stops the resolution: checked before and between the resolver's calls and passed to each.</param>
    /// <param name="generationFrameworkMajor">The request's framework major, supplied only for compilations built by the generator;
    /// <c>null</c> for a program compilation.</param>
    /// <param name="typeOrigins">An optional cache of resolved origins and file keys; every member's identity is checked separately.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    internal static GenerationTargetAnswer Of(IMethodSymbol method, Compilation compilation, ImplementationAssemblies resolver,
                                              CancellationToken cancellationToken, int? generationFrameworkMajor = null, TypeOrigins? typeOrigins = null)
    {
        WorkCancellation.Check();
        cancellationToken.ThrowIfCancellationRequested();
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        if (FunctionPointers.In(definition))
            return new NoGenerationTarget(NoTargetReason.FunctionPointer);
        if (LibraryModels.IsDeclaredInRun(definition))
            return new NoGenerationTarget(NoTargetReason.Source);

        var assembly = definition.ContainingAssembly;
        var origin = compilation.GetMetadataReference(assembly) is PortableExecutableReference { FilePath: { } file } &&
                     ImplementationAssemblies.IsAssemblyName(assembly.Name)
            ? OriginOf(file, resolver, generationFrameworkMajor is not null)
            : null;
        if (origin is null)
            return new NoGenerationTarget(NoTargetReason.OtherPath);
        var major = origin is PackageOrigin ? generationFrameworkMajor ?? FrameworkMajor(compilation, resolver) : null;
        if (origin is PackageOrigin && major is null)
            return new NoGenerationTarget(NoTargetReason.NoFrameworkPack);
        if (definition.GetDocumentationCommentId() is not { } memberId || DocumentationCommentId.CreateDeclarationId(definition) is not { } lookupId ||
            !NamesOnly(definition, memberId, compilation))
            return new NoGenerationTarget(NoTargetReason.AmbiguousId);

        var declaring = definition.ContainingType.OriginalDefinition;
        if (typeOrigins is not null && typeOrigins.Answers.TryGetValue((compilation, declaring), out var cached))
            return cached.ForMember(memberId, lookupId);

        var answer = ResolveType();
        typeOrigins?.Answers.Add((compilation, declaring), TypeOrigin.Of(answer));
        return answer;

        GenerationTargetAnswer ResolveType()
        {
            var version = assembly.Identity.Version;
            WorkCancellation.Check();
            cancellationToken.ThrowIfCancellationRequested();
            switch (origin)
            {
                case PackageOrigin package:
                    var id = PackageId(package, resolver);
                    if (id is null)
                        return new NoGenerationTarget(NoTargetReason.OtherPath);
                    var request = new GenerationRequest(assembly.Name, package.Version.Normalized, memberId, id, $"net{major}.0");
                    var opened = resolver.Resolve(request.AssemblyName, request.Version, request.PackageId, request.Framework, cancellationToken);
                    return Answer(opened, request, lookupId, mvid => GeneratedFileKey.ForPackage(assembly.Name, version, id, package.Version.Normalized, mvid),
                                  definition.ContainingType, cancellationToken);
                case FrameworkOrigin framework:
                    var frameworkRequest = new GenerationRequest(assembly.Name, $"{framework.Major}.0", memberId, null, null);
                    var found = resolver.Resolve(frameworkRequest.AssemblyName, frameworkRequest.Version, null, null, cancellationToken);
                    return Answer(found, frameworkRequest, lookupId, _ => GeneratedFileKey.ForFramework(assembly.Name, version, framework.Name, framework.Major),
                                  definition.ContainingType, cancellationToken);
                default:
                    throw new UnreachableException($"Unknown origin {origin}.");
            }
        }
    }

    /// <summary>The resolved origin and file key per compilation and declaring type; member identities are never cached.</summary>
    internal sealed class TypeOrigins
    {
        internal Dictionary<(Compilation Compilation, INamedTypeSymbol Type), TypeOrigin> Answers { get; } = new();
    }

    /// <summary>The part of a target shared by a type's members.</summary>
    /// <param name="Key">The generated file, or null for a type that is no target.</param>
    /// <param name="Request">The implementation request without a member identity, or null when it cannot be generated.</param>
    /// <param name="Reason">Why the type is no target.</param>
    /// <param name="Detail">What implementation is missing.</param>
    internal sealed record TypeOrigin(GeneratedFileKey? Key, GenerationRequest? Request, NoTargetReason? Reason, string? Detail)
    {
        internal static TypeOrigin Of(GenerationTargetAnswer answer) => answer switch
        {
            GenerationTarget target => new(target.Key, target.Request with { MemberId = "" }, null, null),
            UnavailableGenerationTarget target => new(target.Key, null, null, target.Detail),
            NoGenerationTarget target => new(null, null, target.Reason, null),
            _ => throw new UnreachableException($"Unknown target {answer}.")
        };

        internal GenerationTargetAnswer ForMember(string memberId, string lookupId) => Request is { } request
            ? new GenerationTarget(memberId, lookupId, Key!, request with { MemberId = memberId })
            : Key is { } key ? new UnavailableGenerationTarget(memberId, lookupId, key, Detail!) : new NoGenerationTarget(Reason!.Value);
    }

    /// <summary>Whether a member's declaration id resolves, in the compilation, to exactly one member of its assembly, this one: Roslyn
    /// writes nothing for a function-pointer type, so the id of <c>F(delegate*&lt;void&gt;)</c> is <c>M:P.F()</c>, which finds a
    /// parameterless <c>F()</c> instead.</summary>
    /// <param name="definition">The member's original definition.</param>
    /// <param name="memberId">Its declaration id.</param>
    /// <param name="compilation">The compilation the member belongs to.</param>
    internal static bool NamesOnly(IMethodSymbol definition, string memberId, Compilation compilation) =>
        DocumentationCommentId.GetSymbolsForDeclarationId(memberId, compilation)
                              .Where(symbol => SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, definition.ContainingAssembly))
                              .Take(2)
                              .ToArray() is [{ } only] &&
        SymbolEqualityComparer.Default.Equals(only, definition);

    /// <summary>The answer for what the resolver found.</summary>
    /// <param name="resolution">The resolver's answer.</param>
    /// <param name="request">The generator's request.</param>
    /// <param name="lookupId">The declaration id a call is looked up by.</param>
    /// <param name="key">The file key, from the MVID of the asset the resolution found: the implementation, or the package asset of a target
    /// unavailable for a shared framework it needs; <c>null</c> when the asset is not on disk.</param>
    /// <param name="declaring">The member's declaring type.</param>
    /// <param name="cancellationToken">Stops the read of the implementation's exported types.</param>
    private static GenerationTargetAnswer Answer(ImplementationResolution resolution, GenerationRequest request, string lookupId,
                                                 Func<Guid?, GeneratedFileKey> key, INamedTypeSymbol declaring, CancellationToken cancellationToken)
    {
        if (resolution.Assembly is not { } implementation)
        {
            return resolution.Reason == GenerationReasons.CORELIB
                ? new NoGenerationTarget(NoTargetReason.CoreLib)
                : new UnavailableGenerationTarget(request.MemberId, lookupId, key(resolution.AssetMvid), resolution.Detail);
        }

        WorkCancellation.Check();
        cancellationToken.ThrowIfCancellationRequested();
        return Forwards(implementation.Path, declaring)
            ? new NoGenerationTarget(NoTargetReason.Forwarded)
            : new GenerationTarget(request.MemberId, lookupId, key(implementation.Mvid), request);
    }

    /// <summary>Whether an implementation forwards a type to another assembly: its exported types hold a forwarder of the outermost type
    /// of the declaring type. A file that cannot be read forwards nothing; the generator then reports what it finds.</summary>
    /// <param name="implementationPath">The implementation file.</param>
    /// <param name="declaring">The member's declaring type.</param>
    private static bool Forwards(string implementationPath, INamedTypeSymbol declaring)
    {
        while (declaring.ContainingType is { } outer)
        {
            WorkCancellation.Check();
            declaring = outer;
        }
        var @namespace = declaring.ContainingNamespace.IsGlobalNamespace ? "" : declaring.ContainingNamespace.ToDisplayString();
        try
        {
            using var stream = File.OpenRead(implementationPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return false;
            var reader = pe.GetMetadataReader();
            return reader.ExportedTypes.Select(reader.GetExportedType)
                         .Any(exported => exported.IsForwarder && exported.Implementation.Kind == HandleKind.AssemblyReference &&
                                          reader.GetString(exported.Name) == declaring.MetadataName && reader.GetString(exported.Namespace) == @namespace);
        }
        catch (Exception error) when (error is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The spelling of a package's id for its generated files: the nuspec's, else the folder's, if it is a package id at all.</summary>
    /// <param name="package">The package a reference lies in.</param>
    /// <param name="resolver">Says where the packages folder is.</param>
    private static string? PackageId(PackageOrigin package, ImplementationAssemblies resolver)
    {
        var spelled = ImplementationAssemblies.NuspecId(Path.Combine(resolver.PackagesFolder, package.Folder, package.VersionFolder));
        var id = spelled is not null && string.Equals(spelled, package.Folder, StringComparison.OrdinalIgnoreCase) ? spelled : package.Folder;
        return ImplementationAssemblies.IsPackageId(id) ? id : null;
    }

    /// <summary>The <c>net&lt;major&gt;.0</c> folder of the first <c>Microsoft.NETCore.App.Ref</c> reference of a compilation, or
    /// <c>null</c> when it has none.</summary>
    /// <param name="compilation">The compilation.</param>
    /// <param name="resolver">Says where the reference packs are.</param>
    private static int? FrameworkMajor(Compilation compilation, ImplementationAssemblies resolver)
    {
        foreach (var reference in compilation.References.OfType<PortableExecutableReference>())
        {
            WorkCancellation.Check();
            if (reference.FilePath is { } file && OriginOf(file, resolver) is FrameworkOrigin { Pack: NETCORE_PACK } pack)
                return pack.Major;
        }

        return null;
    }

    /// <summary>Where a metadata reference comes from: a package's <c>lib</c> or <c>ref</c> folder under the packages folder, a shared
    /// framework's reference pack under the dotnet root, or neither (<c>null</c>). In a generator compilation the two supported shared
    /// frameworks' implementation folders are origins too. <c>\</c> and <c>/</c> mean the same, however they are mixed.</summary>
    /// <param name="file">The reference's path.</param>
    /// <param name="resolver">Says where the packages folder and the dotnet root are.</param>
    /// <param name="generationCompilation">Whether shared framework implementation references are targets of a generation.</param>
    private static Origin? OriginOf(string file, ImplementationAssemblies resolver, bool generationCompilation = false)
    {
        var path = Segments(file);
        if (Below(path, Segments(resolver.PackagesFolder)) is { Count: >= 4 } package &&
            (string.Equals(package[2], "lib", StringComparison.OrdinalIgnoreCase) || string.Equals(package[2], "ref", StringComparison.OrdinalIgnoreCase)) &&
            PackageVersion.TryParse(package[1], out var version))
            return new PackageOrigin(package[0], package[1], version);
        if (Below(path, [.. Segments(resolver.DotnetRoot), "packs"]) is { Count: >= 5 } pack && string.Equals(pack[2], "ref", StringComparison.OrdinalIgnoreCase) &&
            ImplementationAssemblies.TryParseFramework(pack[3], out var major))
        {
            if (string.Equals(pack[0], NETCORE_PACK, StringComparison.OrdinalIgnoreCase))
                return new FrameworkOrigin(NETCORE_PACK, NETCORE_FRAMEWORK, major);
            if (string.Equals(pack[0], ASPNETCORE_PACK, StringComparison.OrdinalIgnoreCase))
                return new FrameworkOrigin(ASPNETCORE_PACK, ASPNETCORE_FRAMEWORK, major);
        }
        if (generationCompilation && Below(path, [.. Segments(resolver.DotnetRoot), "shared"]) is { Count: 3 } shared &&
            PackageVersion.TryParse(shared[1], out var patch) && patch.Parts[0] > 0)
        {
            if (string.Equals(shared[0], NETCORE_FRAMEWORK, StringComparison.OrdinalIgnoreCase))
                return new FrameworkOrigin(null, NETCORE_FRAMEWORK, patch.Parts[0]);
            if (string.Equals(shared[0], ASPNETCORE_FRAMEWORK, StringComparison.OrdinalIgnoreCase))
                return new FrameworkOrigin(null, ASPNETCORE_FRAMEWORK, patch.Parts[0]);
        }

        return null;
    }

    /// <summary>The segments of a path, <c>\</c> and <c>/</c> alike, without empty and <c>.</c> segments, a <c>..</c> taking its
    /// parent's place.</summary>
    /// <param name="path">The path.</param>
    private static List<string> Segments(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', '\\'))
        {
            WorkCancellation.Check();
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == ".." && segments.Count > 0)
                segments.RemoveAt(segments.Count - 1);
            else
                segments.Add(segment);
        }

        return segments;
    }

    /// <summary>What lies below a root, compared without regard to case, or <c>null</c> when the path is not below it.</summary>
    /// <param name="path">The path's segments.</param>
    /// <param name="root">The root's segments.</param>
    private static List<string>? Below(List<string> path, IReadOnlyList<string> root) =>
        root.Count > 0 && path.Count > root.Count && root.Zip(path).All(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase))
            ? path.GetRange(root.Count, path.Count - root.Count)
            : null;

    /// <summary>Where a metadata reference comes from.</summary>
    private abstract record Origin;

    /// <summary>A package's <c>lib</c> or <c>ref</c> folder.</summary>
    /// <param name="Folder">The package's folder under the packages folder, its id in lower case.</param>
    /// <param name="VersionFolder">The version's folder.</param>
    /// <param name="Version">The package version the folder names.</param>
    private sealed record PackageOrigin(string Folder, string VersionFolder, PackageVersion Version) : Origin;

    /// <summary>A shared framework's reference pack or implementation folder.</summary>
    /// <param name="Pack">The reference pack, <c>Microsoft.NETCore.App.Ref</c> or <c>Microsoft.AspNetCore.App.Ref</c>;
    /// <c>null</c> for an implementation folder.</param>
    /// <param name="Name">The shared framework.</param>
    /// <param name="Major">The major of the pack's <c>net&lt;major&gt;.0</c> folder or the implementation's patch folder.</param>
    private sealed record FrameworkOrigin(string? Pack, string Name, int Major) : Origin;
}
