using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The implementation assembly of a library member and what it is compiled against.</summary>
/// <param name="AssemblyName">The assembly's name.</param>
/// <param name="Path">The implementation file.</param>
/// <param name="PackageId">The package that ships it, <c>null</c> for a shared framework assembly.</param>
/// <param name="ImplementationVersion">The version folder taken: the package version for a package, the shared framework's patch
/// folder (<c>8.0.31</c>) for a framework assembly.</param>
/// <param name="Framework">The platform used, <c>net8.0</c>.</param>
/// <param name="SharedFrameworkDirectory">The <c>Microsoft.NETCore.App</c> directory of the platform; an assembly of
/// <c>Microsoft.AspNetCore.App</c> and a package that needs it are compiled against that framework's directory too.</param>
/// <param name="References">The files the decompiled assembly is compiled against; empty for a reference assembly.</param>
/// <param name="MissingDependencies">The package dependencies skipped, as <c>&lt;id&gt;@&lt;ranges&gt;</c> or
/// <c>&lt;id&gt;@&lt;version&gt;: no compatible lib folder</c>, and the package dependencies taken below a floor, as
/// <c>&lt;id&gt;@&lt;version&gt;: &lt;assembly&gt; older than &lt;floor&gt;</c>: an assembly of the dependency's lib folder is older than the
/// highest version with which an assembly of the set references it. A dependency of the third form stays in the set.</param>
/// <param name="AssemblyVersion">The assembly version.</param>
/// <param name="FileVersion">The file version.</param>
/// <param name="Mvid">The module version id.</param>
/// <param name="FrameworkName">The shared framework a framework assembly was found in, <c>Microsoft.NETCore.App</c> or
/// <c>Microsoft.AspNetCore.App</c>, with <paramref name="ImplementationVersion"/> its patch; <c>null</c> for a package assembly.</param>
public sealed record ImplementationAssembly(string AssemblyName, string Path, string? PackageId, string ImplementationVersion, string Framework,
                                           string SharedFrameworkDirectory, IReadOnlyList<string> References,
                                           IReadOnlyList<string> MissingDependencies, string AssemblyVersion, string FileVersion, Guid Mvid,
                                           string? FrameworkName = null);

/// <summary>The implementation assembly found, or the reason there is none to decompile.</summary>
/// <param name="Assembly">The assembly opened; set for a reference assembly too, <c>null</c> when none was opened.</param>
/// <param name="Reason">A reason of <see cref="GenerationReasons"/>, <c>null</c> when the assembly can be decompiled.</param>
/// <param name="Detail">Why, in words.</param>
/// <param name="Framework">The platform the resolution chose, <c>net8.0</c>, also when it found no assembly for it; <c>null</c> when
/// it chose none (<c>corelib</c>).</param>
/// <param name="AssetMvid">The module version id of the package asset the platform selects, set whenever the resolution read that asset,
/// also when it opened no assembly because a shared framework the package needs is not installed; <c>null</c> when the package version or
/// its asset is missing, and for a framework member.</param>
public sealed record ImplementationResolution(ImplementationAssembly? Assembly, string? Reason, string Detail, string? Framework,
                                              Guid? AssetMvid = null);

/// <summary>Finds a member's implementation assembly, never a reference assembly (SPEC TD-034b, question 41): a framework member
/// from the installed shared framework of its major version — <c>Microsoft.NETCore.App</c>, else <c>Microsoft.AspNetCore.App</c> —
/// a package member from that exact package version in the NuGet global packages folder. Reads the file system only; nothing is
/// downloaded.</summary>
/// <param name="dotnetRoot">The dotnet root whose <c>shared/Microsoft.NETCore.App</c> and <c>shared/Microsoft.AspNetCore.App</c> hold
/// the shared frameworks.</param>
/// <param name="packagesFolder">The NuGet global packages folder.</param>
/// <param name="runtimeMajor">The platform major of a package member when no target framework is given.</param>
public sealed partial class ImplementationAssemblies(string dotnetRoot, string packagesFolder, int runtimeMajor)
{
    private const string CORELIB = "System.Private.CoreLib";
    private const string SHARED_FRAMEWORK = "Microsoft.NETCore.App";
    private const string ASPNETCORE_FRAMEWORK = "Microsoft.AspNetCore.App";
    internal const int CLOSURE_ROUNDS = 128;

    /// <summary>The floors of a resolution that has none: no assembly is referenced with a version a dependency must reach.</summary>
    private static readonly IReadOnlyDictionary<string, Version> NoFloors = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);

    private static readonly Version NoVersion = new(0, 0, 0, 0);

    /// <summary>Told the path of each nuspec the resolver reads; <c>null</c> in production.</summary>
    internal Action<string>? NuspecRead { get; init; }

    /// <summary>The dotnet root whose <c>shared</c> folder holds the shared frameworks and whose <c>packs</c> folder holds the reference
    /// packs.</summary>
    internal string DotnetRoot => dotnetRoot;

    /// <summary>The NuGet global packages folder.</summary>
    internal string PackagesFolder => packagesFolder;

    /// <summary>The resolver of this process: the dotnet root the running runtime came from, and <c>NUGET_PACKAGES</c> or else
    /// <c>%USERPROFILE%\.nuget\packages</c>.</summary>
    /// <param name="environment">Reads an environment variable; the process environment when <c>null</c>.</param>
    public static ImplementationAssemblies ForThisProcess(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var packages = environment("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(environment("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var dotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        return new ImplementationAssemblies(dotnetRoot, packages, Environment.Version.Major);
    }

    /// <summary>Whether a name is an assembly name: segments of letters, digits, <c>_</c> and <c>-</c> joined by single dots.</summary>
    /// <param name="name">The name.</param>
    public static bool IsAssemblyName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>Whether a name is a NuGet package id: an assembly name of at most 100 characters.</summary>
    /// <param name="id">The id.</param>
    public static bool IsPackageId(string? id) => id is { Length: <= 100 } && NamePattern().IsMatch(id);

    /// <summary>Reads a target framework <c>net&lt;major&gt;.0</c>.</summary>
    /// <param name="framework">The framework text.</param>
    /// <param name="major">Its major version.</param>
    public static bool TryParseFramework(string? framework, out int major)
    {
        major = 0;
        var match = framework is null ? Match.Empty : FrameworkPattern().Match(framework);
        return match.Success && int.TryParse(match.Groups[1].Value, out major) && major > 0;
    }

    /// <summary>Reads the major of a framework member's assembly version: <c>8</c>, <c>8.0</c> and <c>8.0.0.0</c> all mean 8.</summary>
    /// <param name="version">The version text.</param>
    /// <param name="major">Its first component.</param>
    public static bool TryParseAssemblyMajor(string? version, out int major)
    {
        major = 0;
        if (version is null || !AssemblyVersionPattern().IsMatch(version))
            return false;
        return version.Split('.').All(part => int.TryParse(part, out var value) && value <= ushort.MaxValue) &&
               int.TryParse(version.Split('.')[0], out major);
    }

    /// <summary>Finds the implementation assembly of a member. The caller has checked the inputs' grammar; whatever a name holds,
    /// a file outside the folder chosen for it is never opened.</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="version">The assembly version for a framework member, the package version with a package id.</param>
    /// <param name="packageId">The package that ships the assembly; <c>null</c> for a framework member.</param>
    /// <param name="framework">The platform <c>net&lt;major&gt;.0</c> of a package member; <c>null</c> for the running runtime's.</param>
    /// <param name="cancellationToken">Cancels the resolution between nuspec reads and dependency rounds.</param>
    /// <exception cref="ArgumentException">The version or the framework does not parse.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    // bounded: overload forwarding terminates at the implementation overload.
    public ImplementationResolution Resolve(string assemblyName, string version, string? packageId, string? framework,
                                            CancellationToken cancellationToken = default) =>
        Resolve(assemblyName, version, packageId, framework, new GenerationChecks(cancellationToken));

    /// <summary>Finds the implementation assembly of a member as a stage of a generation: it checks at its start, before each nuspec
    /// read and at each dependency round.</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="version">The assembly version for a framework member, the package version with a package id.</param>
    /// <param name="packageId">The package that ships the assembly; <c>null</c> for a framework member.</param>
    /// <param name="framework">The platform <c>net&lt;major&gt;.0</c> of a package member; <c>null</c> for the running runtime's.</param>
    /// <param name="checks">The generation's checks, which cancel the resolution.</param>
    /// <exception cref="ArgumentException">The version or the framework does not parse.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    internal ImplementationResolution Resolve(string assemblyName, string version, string? packageId, string? framework, GenerationChecks checks)
    {
        checks.Check(GenerationStage.Resolve);
        if (string.Equals(assemblyName, CORELIB, StringComparison.OrdinalIgnoreCase))
            return new ImplementationResolution(null, GenerationReasons.CORELIB, "a member of System.Private.CoreLib is out of the generator", null);
        return packageId is null
            ? FrameworkMember(assemblyName, version, checks)
            : PackageMember(assemblyName, version, packageId, framework, checks);
    }

    private ImplementationResolution FrameworkMember(string assemblyName, string version, GenerationChecks checks)
    {
        if (!TryParseAssemblyMajor(version, out var major))
            throw new ArgumentException($"'{version}' is not an assembly version", nameof(version));
        if (SharedFramework(SHARED_FRAMEWORK, major) is not { } shared)
            return Missing($"no shared framework of .NET {major} is installed", major);
        if (Find(shared.Directory, assemblyName) is { } core)
            return Opened(assemblyName, core.Path, core.File, null, shared.Version, major, [shared.Directory], SHARED_FRAMEWORK, [],
                          ClosedDependencies.None, NoFloors, checks).Resolution;
        var web = SharedFramework(ASPNETCORE_FRAMEWORK, major);
        if (web is { } installed && Find(installed.Directory, assemblyName) is { } aspNetCore)
        {
            return Opened(assemblyName, aspNetCore.Path, aspNetCore.File, null, installed.Version, major, [shared.Directory, installed.Directory],
                          ASPNETCORE_FRAMEWORK, [], ClosedDependencies.None, NoFloors, checks).Resolution;
        }

        return Missing(web is { } searched
                           ? $"{assemblyName}.dll is in neither {shared.Directory} nor {searched.Directory}"
                           : $"{assemblyName}.dll is not in {shared.Directory}, and {ASPNETCORE_FRAMEWORK} is not installed for .NET {major}", major);
    }

    private ImplementationResolution PackageMember(string assemblyName, string version, string packageId, string? framework,
                                                   GenerationChecks checks)
    {
        if (!PackageVersion.TryParse(version, out var packageVersion))
            throw new ArgumentException($"'{version}' is not a package version", nameof(version));
        var major = runtimeMajor;
        if (framework is not null && !TryParseFramework(framework, out major))
            throw new ArgumentException($"'{framework}' is not a target framework net<major>.0", nameof(framework));

        var packageDirectory = Path.GetFullPath(Path.Combine(packagesFolder, packageId.ToLowerInvariant(), packageVersion.Normalized));
        if (!Contains(packagesFolder, packageDirectory) || !Directory.Exists(packageDirectory))
            return Missing($"no package folder {packageDirectory}", major);
        if (LibFolder(packageDirectory, major) is not { } lib)
            return Missing($"{packageDirectory} has no lib folder compatible with net{major}.0", major);
        var path = Path.GetFullPath(Path.Combine(lib, assemblyName + ".dll"));
        if (!Contains(lib, path) || !File.Exists(path) || AssemblyFile.Read(path) is not { } file)
            return Missing($"{assemblyName}.dll is not in {lib}", major);
        if (SharedFramework(SHARED_FRAMEWORK, major) is not { } shared)
            return Missing($"no shared framework of .NET {major} is installed", major, file.Mvid);

        var nuspec = ReadNuspec(packageDirectory, major, checks);
        var floors = NoFloors;
        // bounded: at most CLOSURE_ROUNDS rounds, each a Closure that checks at its own rounds.
        for (var round = 1; ; round++)
        {
            var closure = Closure(packageId, nuspec.Dependencies, major, floors, checks);
            var sharedDirectories = new List<string> { shared.Directory };
            if (nuspec.FrameworkReferences.Concat(closure.FrameworkReferences).Contains(ASPNETCORE_FRAMEWORK, StringComparer.OrdinalIgnoreCase))
            {
                if (SharedFramework(ASPNETCORE_FRAMEWORK, major) is not { } web)
                {
                    return Missing($"{packageId} or a package it depends on names the {ASPNETCORE_FRAMEWORK} framework, " +
                                   $"which is not installed for .NET {major}", major, file.Mvid);
                }

                sharedDirectories.Add(web.Directory);
            }

            var opened = Opened(assemblyName, path, file, packageId, packageVersion.Normalized, major, sharedDirectories, null, [lib, .. closure.Folders],
                                closure, floors, checks);
            if (round >= CLOSURE_ROUNDS || !HoldsBelow(closure, floors, opened.Floors))
                return opened.Resolution;
            floors = opened.Floors;
        }
    }

    /// <summary>Whether another round of resolution can change the set: a floor rose, by an assembly a dependency taken holds, and
    /// the dependency's copy is older than the floor now. A floor that rose by a name no dependency taken holds — the reference of
    /// nearly every assembly to <c>System.Private.CoreLib</c> — leaves the set final, so that a resolution with nothing to lift is
    /// one closure and one scan.</summary>
    /// <param name="closure">The dependencies the round took.</param>
    /// <param name="before">The floors the round resolved with.</param>
    /// <param name="after">The floors after the round: those of <paramref name="before"/> raised by the references of its set.</param>
    private static bool HoldsBelow(ClosedDependencies closure, IReadOnlyDictionary<string, Version> before, IReadOnlyDictionary<string, Version> after)
    {
        var raised = after.Where(pair => !before.TryGetValue(pair.Key, out var floor) || pair.Value > floor)
                          .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return raised.Count > 0 && closure.Choices.Any(entry => entry.Choice.Lib is { } lib && Older(lib, raised).Any());
    }

    /// <summary>The resolution of an opened assembly: refused when it is a reference assembly, else with its references — every
    /// other managed assembly of <paramref name="folders"/>, then of the shared frameworks in the order given, an assembly name
    /// taken only once. A name's package copy is the first of the lib folders in order, its framework copy the first of the shared
    /// frameworks in order, so <c>Microsoft.NETCore.App</c>'s wins over <c>Microsoft.AspNetCore.App</c>'s. A name both give is
    /// taken from the higher assembly version, then the higher file version, a tie going to the framework's copy. An assembly the
    /// opened one references that the set lacks is added from the packages folder (<see cref="Unprovided"/>). The floors are raised by
    /// the AssemblyRef rows of the opened assembly and of the references taken — never of a copy that lost a name, and never by a
    /// row of version <c>0.0.0.0</c> — and the dependencies of <paramref name="closure"/> are named against the raised floors.</summary>
    /// <param name="assemblyName">The assembly name asked for.</param>
    /// <param name="path">The implementation file.</param>
    /// <param name="file">Its metadata.</param>
    /// <param name="packageId">The package that ships it, or <c>null</c>.</param>
    /// <param name="implementationVersion">The version folder taken.</param>
    /// <param name="major">The platform major.</param>
    /// <param name="sharedDirectories">The shared framework directories the assembly is compiled against: the platform's
    /// <c>Microsoft.NETCore.App</c> first, then <c>Microsoft.AspNetCore.App</c> when the assembly or the package needs it.</param>
    /// <param name="frameworkName">The shared framework a framework assembly was found in; <c>null</c> for a package assembly.</param>
    /// <param name="folders">The package's lib folder and its dependencies' lib folders, in that order; empty for a framework
    /// member.</param>
    /// <param name="closure">The dependencies of the package, which name the ones skipped and the ones taken below a floor in
    /// <see cref="ImplementationAssembly.MissingDependencies"/>; none for a framework member.</param>
    /// <param name="floors">The floors the dependencies were resolved with: the highest version, by assembly name, with which an
    /// assembly of an earlier round's set references it.</param>
    /// <param name="checks">The generation's checks; cancel before the metadata of each candidate reference is read.</param>
    private OpenedAssembly Opened(string assemblyName, string path, AssemblyFile file, string? packageId, string implementationVersion,
                                  int major, IReadOnlyList<string> sharedDirectories, string? frameworkName, IReadOnlyList<string> folders,
                                  ClosedDependencies closure, IReadOnlyDictionary<string, Version> floors, GenerationChecks checks)
    {
        var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
        if (file.IsReferenceAssembly)
        {
            return new OpenedAssembly(
                new ImplementationResolution(
                    new ImplementationAssembly(assemblyName, path, packageId, implementationVersion, $"net{major}.0", sharedDirectories[0], [],
                                               closure.MissingDependencies(floors), file.Version.ToString(), fileVersion, file.Mvid, frameworkName),
                    GenerationReasons.REFERENCE_ASSEMBLY, $"{path} is a reference assembly", $"net{major}.0"),
                floors);
        }

        var packageCopies = new Dictionary<string, Copy>(StringComparer.OrdinalIgnoreCase);
        var frameworkCopies = new Dictionary<string, Copy>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        foreach (var (folder, isFramework) in folders.Select(folder => (folder, false)).Concat(sharedDirectories.Select(folder => (folder, true))))
        {
            WorkCancellation.Check();
            foreach (var candidate in Directory.EnumerateFiles(folder, "*.dll").Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase))
            {
                checks.Check(GenerationStage.Resolve);
                if (!Contains(folder, candidate) || string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (AssemblyFile.Read(candidate) is { } reference && !string.Equals(reference.Name, file.Name, StringComparison.OrdinalIgnoreCase))
                    (isFramework ? frameworkCopies : packageCopies).TryAdd(reference.Name, new Copy(candidate, reference, position++));
            }
        }

        var taken = packageCopies.Values
                                 .Select(package => frameworkCopies.TryGetValue(package.File.Name, out var framework) ? Winner(package, framework) : package)
                                 .Concat(frameworkCopies.Values.Where(framework => !packageCopies.ContainsKey(framework.File.Name)))
                                 .OrderBy(copy => copy.Position)
                                 .ToList();
        var provided = taken.Select(copy => copy.File.Name).Append(file.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in file.References.OrderByDescending(row => row.Version)
                                      .DistinctBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                                      .Where(row => !provided.Contains(row.Name)))
        {
            WorkCancellation.Check();
            if (Unprovided(reference, major, position, checks) is { } found)
            {
                taken.Add(found);
                position++;
            }
        }

        var raised = Raised(floors, file.References.Concat(taken.SelectMany(copy => copy.File.References)));
        return new OpenedAssembly(
            new ImplementationResolution(
                new ImplementationAssembly(assemblyName, path, packageId, implementationVersion, $"net{major}.0", sharedDirectories[0],
                                           taken.Select(copy => copy.Path).ToList(), closure.MissingDependencies(raised), file.Version.ToString(),
                                           fileVersion, file.Mvid, frameworkName),
                null, $"{path}", $"net{major}.0"),
            raised);
    }

    /// <summary>The floors after a set: each name at the higher of its floor and the highest version with which a row references it.
    /// A row of version <c>0.0.0.0</c> names no version and raises nothing.</summary>
    /// <param name="floors">The floors so far; never lowered.</param>
    /// <param name="rows">The AssemblyRef rows of the assemblies of the set.</param>
    private static Dictionary<string, Version> Raised(IReadOnlyDictionary<string, Version> floors, IEnumerable<AssemblyReference> rows)
    {
        var raised = new Dictionary<string, Version>(floors, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            WorkCancellation.Check();
            if (row.Version > NoVersion && (!raised.TryGetValue(row.Name, out var floor) || row.Version > floor))
                raised[row.Name] = row.Version;
        }

        return raised;
    }

    /// <summary>The one file of the packages folder that gives an assembly the opened one references and its set lacks: the package
    /// of that id — the assembly's simple name — at the lowest cached version whose lib folder for the platform holds the assembly at
    /// a version not older than the reference. Nothing is found for a name that is no package id, a package that is not cached, or
    /// no cached version that reaches the reference; nothing is downloaded.</summary>
    /// <param name="reference">The AssemblyRef row of the opened assembly.</param>
    /// <param name="major">The platform major.</param>
    /// <param name="position">The position the file takes among the references.</param>
    /// <param name="checks">The generation's checks; cancel before the metadata of each cached version's file is read.</param>
    private Copy? Unprovided(AssemblyReference reference, int major, int position, GenerationChecks checks)
    {
        if (!IsPackageId(reference.Name))
            return null;
        var idDirectory = Path.GetFullPath(Path.Combine(packagesFolder, reference.Name.ToLowerInvariant()));
        if (!Contains(packagesFolder, idDirectory) || !Directory.Exists(idDirectory))
            return null;
        foreach (var (directory, _) in CachedVersions(idDirectory))
        {
            checks.Check(GenerationStage.Resolve);
            if (LibFolder(directory, major) is not { } lib)
                continue;
            var path = Path.GetFullPath(Path.Combine(lib, reference.Name + ".dll"));
            if (Contains(lib, path) && File.Exists(path) && AssemblyFile.Read(path) is { } file &&
                string.Equals(file.Name, reference.Name, StringComparison.OrdinalIgnoreCase) && file.Version >= reference.Version)
                return new Copy(path, file, position);
        }

        return null;
    }

    /// <summary>Which of a package's and a shared framework's copies of one assembly name is compiled against: the higher assembly
    /// version, then the higher file version, a tie going to the framework's copy. The file version is read only on a tie of the
    /// assembly versions.</summary>
    /// <param name="package">The package's copy.</param>
    /// <param name="framework">The shared framework's copy.</param>
    private static Copy Winner(Copy package, Copy framework)
    {
        var byAssembly = package.File.Version.CompareTo(framework.File.Version);
        if (byAssembly != 0)
            return byAssembly > 0 ? package : framework;
        return FileVersion(package.Path).CompareTo(FileVersion(framework.Path)) > 0 ? package : framework;
    }

    /// <summary>The file version a file's version resource gives, or <c>0.0.0.0</c> without one.</summary>
    /// <param name="path">The file.</param>
    private static Version FileVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    private static ImplementationResolution Missing(string detail, int major, Guid? assetMvid = null) =>
        new(null, GenerationReasons.NO_IMPLEMENTATION, detail, $"net{major}.0", assetMvid);

    /// <summary>The managed assembly of a name in a shared framework's patch folder.</summary>
    /// <param name="directory">The shared framework's patch folder.</param>
    /// <param name="assemblyName">The assembly name.</param>
    private static (string Path, AssemblyFile File)? Find(string directory, string assemblyName)
    {
        var path = Path.GetFullPath(Path.Combine(directory, assemblyName + ".dll"));
        return Contains(directory, path) && File.Exists(path) && AssemblyFile.Read(path) is { } file ? (path, file) : null;
    }

    /// <summary>The highest installed patch of a major's shared framework, never another major.</summary>
    /// <param name="framework">The shared framework: <c>Microsoft.NETCore.App</c> or <c>Microsoft.AspNetCore.App</c>.</param>
    /// <param name="major">The platform major.</param>
    private (string Directory, string Version)? SharedFramework(string framework, int major)
    {
        var root = Path.Combine(dotnetRoot, "shared", framework);
        if (!Directory.Exists(root))
            return null;
        var best = Directory.EnumerateDirectories(root)
                            .Select(directory => (Directory: Path.GetFullPath(directory), Name: Path.GetFileName(directory)))
                            .Select(entry => (entry.Directory, entry.Name, Parsed: PackageVersion.TryParse(entry.Name, out var parsed) ? parsed : null))
                            .Where(entry => entry.Parsed is not null && entry.Parsed.Parts[0] == major && Contains(root, entry.Directory))
                            .OrderByDescending(entry => entry.Parsed)
                            .FirstOrDefault();
        return best.Parsed is null ? null : (best.Directory, best.Name);
    }

    /// <summary>The lib folder of a package nearest to <c>net&lt;major&gt;.0</c>, or <c>null</c>.</summary>
    /// <param name="packageDirectory">The package's version folder.</param>
    /// <param name="major">The platform major.</param>
    private static string? LibFolder(string packageDirectory, int major)
    {
        var lib = Path.Combine(packageDirectory, "lib");
        if (!Directory.Exists(lib))
            return null;
        var folders = Directory.EnumerateDirectories(lib).ToDictionary(folder => Path.GetFileName(folder), Path.GetFullPath, StringComparer.OrdinalIgnoreCase);
        return Nearest(folders.Keys, major) is { } chosen && Contains(packageDirectory, folders[chosen]) ? folders[chosen] : null;
    }

    /// <summary>The framework nearest to <c>net&lt;major&gt;.0</c> among short framework names: <c>net&lt;major&gt;.0</c> itself, else
    /// the highest <c>netY.0</c> with Y below it, else <c>netstandard2.1</c>, <c>netstandard2.0</c>, <c>netstandard1.*</c> from the
    /// highest; never a .NET Framework name.</summary>
    /// <param name="names">The short framework names offered.</param>
    /// <param name="major">The platform major.</param>
    internal static string? Nearest(IEnumerable<string> names, int major)
    {
        var offered = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preference = Enumerable.Range(5, Math.Max(0, major - 4)).Reverse().Select(net => $"net{net}.0")
                                   .Concat(["netstandard2.1", "netstandard2.0"])
                                   .Concat(Enumerable.Range(0, 7).Reverse().Select(minor => $"netstandard1.{minor}"));
        return preference.FirstOrDefault(offered.Contains) is { } chosen ? offered.First(name => name.Equals(chosen, StringComparison.OrdinalIgnoreCase)) : null;
    }

    /// <summary>A nuspec's target framework as NuGet's short folder name: <c>.NETStandard2.0</c> is <c>netstandard2.0</c>,
    /// <c>.NETFramework4.6.1</c> is <c>net461</c>, <c>.NETCoreApp5.0</c> is <c>net5.0</c>.</summary>
    /// <param name="targetFramework">The nuspec's <c>targetFramework</c>.</param>
    internal static string ShortFrameworkName(string targetFramework)
    {
        var text = targetFramework.Trim().ToLowerInvariant().Replace(",version=v", "", StringComparison.Ordinal);
        if (text.StartsWith(".netstandard", StringComparison.Ordinal))
            return "netstandard" + text[".netstandard".Length..];
        if (text.StartsWith(".netframework", StringComparison.Ordinal))
            return "net" + text[".netframework".Length..].Replace(".", "", StringComparison.Ordinal);
        foreach (var prefix in new[] { ".netcoreapp", "netcoreapp" })
        {
            WorkCancellation.Check();
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var version = text[prefix.Length..];
            return int.TryParse(version.Split('.')[0], out var major) && major >= 5 ? "net" + version : "netcoreapp" + version;
        }

        return text;
    }

    /// <summary>What a package's nuspec lists for the framework nearest to the platform: its dependencies and the shared frameworks
    /// it references, each from the nearest group, from a group without a framework when none is near, or from the whole list
    /// when it has no groups.</summary>
    /// <param name="packageDirectory">The package's version folder.</param>
    /// <param name="major">The platform major.</param>
    /// <param name="checks">The generation's checks; cancels before the nuspec is read.</param>
    private Nuspec ReadNuspec(string packageDirectory, int major, GenerationChecks checks)
    {
        checks.Check(GenerationStage.Resolve);
        var nuspec = Directory.EnumerateFiles(packageDirectory, "*.nuspec").Select(Path.GetFullPath).Order(StringComparer.Ordinal).FirstOrDefault();
        if (nuspec is null || !Contains(packageDirectory, nuspec))
            return Nuspec.Empty;
        NuspecRead?.Invoke(nuspec);
        var document = XDocument.Load(nuspec);
        var dependencies = ChosenGroup(document.Descendants().FirstOrDefault(element => element.Name.LocalName == "dependencies"), major)
                               ?.Elements().Where(element => element.Name.LocalName == "dependency" && element.Attribute("id") is not null)
                               .Select(element =>
                               {
                                   var text = element.Attribute("version")?.Value ?? "0.0.0";
                                   return new Dependency(element.Attribute("id")!.Value, text, PackageVersionRange.TryParse(text, out var range) ? range : null);
                               })
                               .ToArray() ?? [];
        var frameworks = ChosenGroup(document.Descendants().FirstOrDefault(element => element.Name.LocalName == "frameworkReferences"), major)
                             ?.Elements().Where(element => element.Name.LocalName == "frameworkReference" && element.Attribute("name") is not null)
                             .Select(element => element.Attribute("name")!.Value)
                             .ToArray() ?? [];
        return new Nuspec(dependencies, frameworks);
    }

    /// <summary>The id of a package as its nuspec spells it, or <c>null</c> when the folder has no nuspec that can be read or it names no
    /// id. The folder under the packages folder is the id in lower case; the spelling is the one the generated files are named by.</summary>
    /// <param name="packageDirectory">The package's version folder.</param>
    internal static string? NuspecId(string packageDirectory)
    {
        try
        {
            if (!Directory.Exists(packageDirectory) ||
                Directory.EnumerateFiles(packageDirectory, "*.nuspec").Select(Path.GetFullPath).Order(StringComparer.Ordinal).FirstOrDefault() is not { } nuspec)
                return null;
            var metadata = XDocument.Load(nuspec).Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "metadata");
            return metadata?.Elements().FirstOrDefault(element => element.Name.LocalName == "id")?.Value.Trim() is { Length: > 0 } id ? id : null;
        }
        catch (Exception error) when (error is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The group of a nuspec list that applies to the platform: the group of the framework nearest to it, a group without
    /// a framework when none is near, the list itself when it has no groups.</summary>
    /// <param name="list">The <c>dependencies</c> or <c>frameworkReferences</c> element, or <c>null</c> when the nuspec has none.</param>
    /// <param name="major">The platform major.</param>
    private static XElement? ChosenGroup(XElement? list, int major)
    {
        if (list is null)
            return null;
        var groups = list.Elements().Where(element => element.Name.LocalName == "group").ToArray();
        if (groups.Length == 0)
            return list;
        var named = groups.Where(group => group.Attribute("targetFramework") is not null)
                          .GroupBy(group => ShortFrameworkName(group.Attribute("targetFramework")!.Value), StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return Nearest(named.Keys, major) is { } nearest ? named[nearest] : groups.FirstOrDefault(group => group.Attribute("targetFramework") is null);
    }

    /// <summary>Resolves the package's dependencies, recursively, to a fixpoint: each id once for the whole closure, at the lowest
    /// version in the packages folder that every range reaching it admits and whose assemblies the floors name are not older than
    /// them. A dependency no cached version satisfies, or whose chosen version has no compatible lib folder, is skipped and named,
    /// and its own dependencies are not followed. A dependency no admitted version of which reaches the floors stays at the lowest
    /// admitted version, in the set, and is named with the floor (<see cref="ClosedDependencies.MissingDependencies"/>). When no
    /// fixpoint comes — a set of choices repeats (the ranges oscillate) or <see cref="CLOSURE_ROUNDS"/> rounds pass — every id whose
    /// choice still changes is skipped and named with its ranges, round after round, until the choices left are the ones every range
    /// reaching them admits: a version outside a range is never taken, and no dependency is dropped without a name. The shared
    /// frameworks the dependencies taken reference are returned with the folders. The floors are fixed for the whole call: a rise
    /// of a floor is a new call, with a new cache of choices.</summary>
    /// <param name="packageId">The package whose closure it is; a dependency back on it is ignored.</param>
    /// <param name="direct">The package's own dependencies.</param>
    /// <param name="major">The platform major.</param>
    /// <param name="floors">The floors by assembly name: the highest version with which an assembly of an earlier round's set
    /// references it.</param>
    /// <param name="checks">The generation's checks; cancels the closure before a round and before a nuspec is read.</param>
    private ClosedDependencies Closure(string packageId, IReadOnlyList<Dependency> direct, int major, IReadOnlyDictionary<string, Version> floors,
                                       GenerationChecks checks)
    {
        var chosen = new Dictionary<string, Choice>(StringComparer.OrdinalIgnoreCase);
        var choices = new Dictionary<string, Choice>(StringComparer.OrdinalIgnoreCase);
        var states = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unsettled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var round = 0; ; round++)
        {
            checks.Check(GenerationStage.Resolve);
            var ranges = new Dictionary<string, List<Dependency>>(StringComparer.OrdinalIgnoreCase);
            var followed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<IReadOnlyList<Dependency>>([direct]);
            while (pending.TryDequeue(out var dependencies))
            {
                WorkCancellation.Check();
                foreach (var dependency in dependencies)
                {
                    WorkCancellation.Check();
                    if (string.Equals(dependency.Id, packageId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!ranges.TryGetValue(dependency.Id, out var reaching))
                        ranges[dependency.Id] = reaching = [];
                    reaching.Add(dependency);
                    if (!unsettled.Contains(dependency.Id) && chosen.TryGetValue(dependency.Id, out var choice) && choice.Lib is not null &&
                        followed.Add(dependency.Id))
                        pending.Enqueue(choice.Nuspec.Dependencies);
                }
            }

            var next = ranges.ToDictionary(pair => pair.Key,
                                           pair => unsettled.Contains(pair.Key)
                                               ? Unsatisfied(pair.Key, pair.Value)
                                               : Choose(pair.Key, pair.Value, major, floors, choices, checks),
                                           StringComparer.OrdinalIgnoreCase);
            var changed = next.Where(pair => !unsettled.Contains(pair.Key) &&
                                             !(chosen.TryGetValue(pair.Key, out var before) && before.Version == pair.Value.Version &&
                                               before.Missing == pair.Value.Missing))
                              .Select(pair => pair.Key)
                              .ToArray();
            chosen = next;
            if (changed.Length == 0)
                break;
            var state = string.Join("\n", next.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                                              .Select(pair => $"{pair.Key}={pair.Value.Version}|{pair.Value.Missing}"));
            if (unsettled.Count > 0 || !states.Add(state) || round + 1 >= CLOSURE_ROUNDS)
                unsettled.UnionWith(changed);
        }

        var ordered = chosen.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => (Id: pair.Key, Choice: pair.Value)).ToArray();
        return new ClosedDependencies(ordered.Where(entry => entry.Choice.Lib is not null).Select(entry => entry.Choice.Lib!).ToArray(), ordered,
                                      ordered.Where(entry => entry.Choice.Lib is not null).SelectMany(entry => entry.Choice.Nuspec.FrameworkReferences)
                                             .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>A dependency skipped and named with the ranges that reach it.</summary>
    /// <param name="id">The dependency id.</param>
    /// <param name="reaching">The dependencies on it that reach it.</param>
    private static Choice Unsatisfied(string id, IReadOnlyList<Dependency> reaching) =>
        new(null, null, Nuspec.Empty, $"{id}@{string.Join(" ", reaching.Select(dependency => dependency.Text).Distinct(StringComparer.Ordinal))}");

    /// <summary>The lowest cached version of a dependency every range reaching it admits and the floors do not pass, read once per id
    /// and ranges; the floors are those of the closure, so the cache is the closure's.</summary>
    /// <param name="id">The dependency id.</param>
    /// <param name="reaching">The dependencies on it that reach it.</param>
    /// <param name="major">The platform major.</param>
    /// <param name="floors">The floors by assembly name.</param>
    /// <param name="choices">The choices already made in this closure, by id and ranges.</param>
    /// <param name="checks">The generation's checks; cancels before the chosen version's nuspec is read.</param>
    private Choice Choose(string id, IReadOnlyList<Dependency> reaching, int major, IReadOnlyDictionary<string, Version> floors,
                          Dictionary<string, Choice> choices, GenerationChecks checks)
    {
        var unsatisfied = Unsatisfied(id, reaching);
        if (choices.TryGetValue(unsatisfied.Missing!, out var known))
            return known;
        return choices[unsatisfied.Missing!] = Lowest(id, reaching, major, unsatisfied, floors, checks);
    }

    /// <summary>The lowest cached version every range admits whose lib folder holds no assembly a floor names below the floor. A
    /// version without a compatible lib folder reaches every floor and is taken, named, as it always was. When no admitted version
    /// reaches the floors, the lowest admitted one is taken with its lib folder.</summary>
    /// <param name="id">The dependency id.</param>
    /// <param name="reaching">The dependencies on it that reach it.</param>
    /// <param name="major">The platform major.</param>
    /// <param name="unsatisfied">The choice that skips the dependency, for when no cached version is admitted.</param>
    /// <param name="floors">The floors by assembly name.</param>
    /// <param name="checks">The generation's checks; cancels before the chosen version's nuspec is read.</param>
    private Choice Lowest(string id, IReadOnlyList<Dependency> reaching, int major, Choice unsatisfied, IReadOnlyDictionary<string, Version> floors,
                          GenerationChecks checks)
    {
        var idDirectory = Path.GetFullPath(Path.Combine(packagesFolder, id.ToLowerInvariant()));
        if (!IsPackageId(id) || reaching.Any(dependency => dependency.Range is null) || !Contains(packagesFolder, idDirectory) ||
            !Directory.Exists(idDirectory))
            return unsatisfied;
        var admitted = CachedVersions(idDirectory).Where(entry => reaching.All(dependency => dependency.Range!.Admits(entry.Version)));
        (string Directory, PackageVersion Version, string Lib)? lowest = null;
        foreach (var (directory, version) in admitted)
        {
            WorkCancellation.Check();
            if (LibFolder(directory, major) is not { } lib)
                return new Choice(version.Normalized, null, Nuspec.Empty, $"{id}@{version.Normalized}: no compatible lib folder");
            lowest ??= (directory, version, lib);
            if (!Older(lib, floors).Any())
                return new Choice(version.Normalized, lib, ReadNuspec(directory, major, checks), null);
        }

        return lowest is { } below ? new Choice(below.Version.Normalized, below.Lib, ReadNuspec(below.Directory, major, checks), null) : unsatisfied;
    }

    /// <summary>The cached versions of a package id, lowest first; a folder whose name is no version is none.</summary>
    /// <param name="idDirectory">The package id's folder in the packages folder.</param>
    private static IEnumerable<(string Directory, PackageVersion Version)> CachedVersions(string idDirectory) =>
        Directory.EnumerateDirectories(idDirectory)
                 .Select(directory => (Directory: Path.GetFullPath(directory),
                                       Version: PackageVersion.TryParse(Path.GetFileName(directory), out var parsed) ? parsed : null))
                 .Where(entry => entry.Version is not null)
                 .Select(entry => (entry.Directory, Version: entry.Version!))
                 .OrderBy(entry => entry.Version);

    /// <summary>The assemblies of a lib folder that a floor names, held below it: the one answer to whether a dependency reaches the
    /// floors, for the choice of its version, for its name in the record and for whether another round is worth taking.</summary>
    /// <param name="lib">The lib folder.</param>
    /// <param name="floors">The floors by assembly name; with none, nothing is read.</param>
    private static IEnumerable<Shortfall> Older(string lib, IReadOnlyDictionary<string, Version> floors)
    {
        if (floors.Count == 0)
            yield break;
        foreach (var candidate in Directory.EnumerateFiles(lib, "*.dll").Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase))
        {
            WorkCancellation.Check();
            if (Contains(lib, candidate) && AssemblyFile.Read(candidate) is { } held && floors.TryGetValue(held.Name, out var floor) && held.Version < floor)
                yield return new Shortfall(held.Name, floor);
        }
    }

    /// <summary>Whether a full path lies under a directory.</summary>
    /// <param name="directory">The directory.</param>
    /// <param name="path">The path.</param>
    private static bool Contains(string directory, string path) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar,
                                          StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^net([1-9][0-9]*)\.0$")]
    private static partial Regex FrameworkPattern();

    [GeneratedRegex(@"^[0-9]+(\.[0-9]+){0,3}$")]
    private static partial Regex AssemblyVersionPattern();

    private sealed record Dependency(string Id, string Text, PackageVersionRange? Range);

    /// <summary>What a package's dependency closure comes to.</summary>
    /// <param name="Folders">The lib folders of the dependencies taken, by id.</param>
    /// <param name="Choices">Every dependency of the closure, by id: taken at a version with its lib folder, or skipped and named.</param>
    /// <param name="FrameworkReferences">The shared frameworks the dependencies taken reference, each once.</param>
    private sealed record ClosedDependencies(IReadOnlyList<string> Folders, IReadOnlyList<(string Id, Choice Choice)> Choices,
                                             IReadOnlyList<string> FrameworkReferences)
    {
        public static readonly ClosedDependencies None = new([], [], []);

        /// <summary>The dependencies skipped, named as the closure named them, and the dependencies taken whose lib folder holds an
        /// assembly older than a floor, named with the assembly and the floor, in the order of the ids.</summary>
        /// <param name="floors">The floors by assembly name: the highest version with which an assembly of the set references it.</param>
        public IReadOnlyList<string> MissingDependencies(IReadOnlyDictionary<string, Version> floors) =>
            Choices.Select(entry => entry.Choice.Missing ??
                                    (entry.Choice.Lib is { } lib && Older(lib, floors).FirstOrDefault() is { } below
                                        ? $"{entry.Id}@{entry.Choice.Version}: {below.Assembly} older than {below.Floor}"
                                        : null))
                   .OfType<string>()
                   .ToArray();
    }

    /// <summary>An assembly of a dependency's lib folder that is older than a floor.</summary>
    /// <param name="Assembly">The assembly name.</param>
    /// <param name="Floor">The highest version with which an assembly of the set references it.</param>
    private sealed record Shortfall(string Assembly, Version Floor);

    /// <summary>An opened assembly and the floors its set raised.</summary>
    /// <param name="Resolution">The resolution.</param>
    /// <param name="Floors">The floors it was resolved with, raised by the references of its set.</param>
    private sealed record OpenedAssembly(ImplementationResolution Resolution, IReadOnlyDictionary<string, Version> Floors);

    /// <summary>What a nuspec lists for the platform.</summary>
    /// <param name="Dependencies">The dependencies of the chosen group.</param>
    /// <param name="FrameworkReferences">The names of the shared frameworks the chosen group references.</param>
    private sealed record Nuspec(IReadOnlyList<Dependency> Dependencies, IReadOnlyList<string> FrameworkReferences)
    {
        public static readonly Nuspec Empty = new([], []);
    }

    private sealed record Choice(string? Version, string? Lib, Nuspec Nuspec, string? Missing);

    /// <summary>A candidate reference found while scanning the folders of a resolution.</summary>
    /// <param name="Path">The file.</param>
    /// <param name="File">Its metadata.</param>
    /// <param name="Position">Where the scan met it, to keep the references in the order of the scan.</param>
    private sealed record Copy(string Path, AssemblyFile File, int Position);
}

/// <summary>One AssemblyRef row of a managed assembly: an assembly it references, with the version it references.</summary>
/// <param name="Name">The referenced assembly's simple name.</param>
/// <param name="Version">The version the row names.</param>
internal sealed record AssemblyReference(string Name, Version Version);

/// <summary>What the resolver reads of a managed assembly's metadata.</summary>
/// <param name="Name">The assembly name.</param>
/// <param name="Version">The assembly version.</param>
/// <param name="IsReferenceAssembly">Whether it carries <c>ReferenceAssemblyAttribute</c>.</param>
/// <param name="Mvid">The module version id.</param>
/// <param name="References">Its AssemblyRef rows, in the order of the table: each assembly it references, with the version it references.</param>
internal sealed record AssemblyFile(string Name, Version Version, bool IsReferenceAssembly, Guid Mvid, IReadOnlyList<AssemblyReference> References)
{
    private const string REFERENCE_ASSEMBLY_ATTRIBUTE = "System.Runtime.CompilerServices.ReferenceAssemblyAttribute";

    /// <summary>Reads a file's assembly metadata, or <c>null</c> when it is not a managed assembly.</summary>
    /// <param name="path">The file.</param>
    public static AssemblyFile? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return null;
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
                return null;
            var definition = reader.GetAssemblyDefinition();
            var isReference = definition.GetCustomAttributes().Any(handle => AttributeName(reader, reader.GetCustomAttribute(handle)) == REFERENCE_ASSEMBLY_ATTRIBUTE);
            var references = reader.AssemblyReferences
                                   .Select(handle => reader.GetAssemblyReference(handle))
                                   .Select(row => new AssemblyReference(reader.GetString(row.Name), row.Version))
                                   .ToList();
            return new AssemblyFile(reader.GetString(definition.Name), definition.Version, isReference,
                                    reader.GetGuid(reader.GetModuleDefinition().Mvid), references);
        }
        catch (Exception error) when (error is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string AttributeName(MetadataReader reader, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                if (parent.Kind != HandleKind.TypeReference)
                    return "";
                var reference = reader.GetTypeReference((TypeReferenceHandle)parent);
                return reader.GetString(reference.Namespace) + "." + reader.GetString(reference.Name);
            case HandleKind.MethodDefinition:
                var type = reader.GetTypeDefinition(reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType());
                return reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
            default:
                return "";
        }
    }
}
