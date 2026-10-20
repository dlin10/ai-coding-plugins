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
/// <c>&lt;id&gt;@&lt;version&gt;: no compatible lib folder</c>.</param>
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
            return Opened(assemblyName, core.Path, core.File, null, shared.Version, major, [shared.Directory], SHARED_FRAMEWORK, [], [], checks);
        var web = SharedFramework(ASPNETCORE_FRAMEWORK, major);
        if (web is { } installed && Find(installed.Directory, assemblyName) is { } aspNetCore)
        {
            return Opened(assemblyName, aspNetCore.Path, aspNetCore.File, null, installed.Version, major, [shared.Directory, installed.Directory],
                          ASPNETCORE_FRAMEWORK, [], [], checks);
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
        var closure = Closure(packageId, nuspec.Dependencies, major, checks);
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

        return Opened(assemblyName, path, file, packageId, packageVersion.Normalized, major, sharedDirectories, null, [lib, .. closure.Folders],
                      closure.Missing, checks);
    }

    /// <summary>The resolution of an opened assembly: refused when it is a reference assembly, else with its references — every
    /// other managed assembly of <paramref name="folders"/>, then of the shared frameworks in the order given, an assembly name
    /// taken only once, so a package's copy wins over a framework's and <c>Microsoft.NETCore.App</c>'s over
    /// <c>Microsoft.AspNetCore.App</c>'s.</summary>
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
    /// <param name="missing">The dependencies skipped.</param>
    /// <param name="checks">The generation's checks; cancel before the metadata of each candidate reference is read.</param>
    private static ImplementationResolution Opened(string assemblyName, string path, AssemblyFile file, string? packageId,
                                                   string implementationVersion, int major, IReadOnlyList<string> sharedDirectories,
                                                   string? frameworkName, IReadOnlyList<string> folders, IReadOnlyList<string> missing,
                                                   GenerationChecks checks)
    {
        var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
        if (file.IsReferenceAssembly)
        {
            return new ImplementationResolution(
                new ImplementationAssembly(assemblyName, path, packageId, implementationVersion, $"net{major}.0", sharedDirectories[0], [], missing,
                                           file.Version.ToString(), fileVersion, file.Mvid, frameworkName),
                GenerationReasons.REFERENCE_ASSEMBLY, $"{path} is a reference assembly", $"net{major}.0");
        }

        var supplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file.Name };
        var references = new List<string>();
        foreach (var folder in folders.Concat(sharedDirectories))
        {
            WorkCancellation.Check();
            foreach (var candidate in Directory.EnumerateFiles(folder, "*.dll").Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase))
            {
                checks.Check(GenerationStage.Resolve);
                if (!Contains(folder, candidate) || string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (AssemblyFile.Read(candidate) is { } reference && supplied.Add(reference.Name))
                    references.Add(candidate);
            }
        }

        return new ImplementationResolution(
            new ImplementationAssembly(assemblyName, path, packageId, implementationVersion, $"net{major}.0", sharedDirectories[0], references, missing,
                                       file.Version.ToString(), fileVersion, file.Mvid, frameworkName),
            null, $"{path}", $"net{major}.0");
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
    /// version in the packages folder that every range reaching it admits. A dependency no cached version satisfies, or whose
    /// chosen version has no compatible lib folder, is skipped and named, and its own dependencies are not followed. When no
    /// fixpoint comes — a set of choices repeats (the ranges oscillate) or <see cref="CLOSURE_ROUNDS"/> rounds pass — every id whose
    /// choice still changes is skipped and named with its ranges, round after round, until the choices left are the ones every range
    /// reaching them admits: a version outside a range is never taken, and no dependency is dropped without a name. The shared
    /// frameworks the dependencies taken reference are returned with the folders.</summary>
    /// <param name="packageId">The package whose closure it is; a dependency back on it is ignored.</param>
    /// <param name="direct">The package's own dependencies.</param>
    /// <param name="major">The platform major.</param>
    /// <param name="checks">The generation's checks; cancels the closure before a round and before a nuspec is read.</param>
    private ClosedDependencies Closure(string packageId, IReadOnlyList<Dependency> direct, int major, GenerationChecks checks)
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
                                               : Choose(pair.Key, pair.Value, major, choices, checks),
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

        var ordered = chosen.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Value).ToArray();
        return new ClosedDependencies(ordered.Where(choice => choice.Lib is not null).Select(choice => choice.Lib!).ToArray(),
                                      ordered.Where(choice => choice.Missing is not null).Select(choice => choice.Missing!).ToArray(),
                                      ordered.Where(choice => choice.Lib is not null).SelectMany(choice => choice.Nuspec.FrameworkReferences)
                                             .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>A dependency skipped and named with the ranges that reach it.</summary>
    /// <param name="id">The dependency id.</param>
    /// <param name="reaching">The dependencies on it that reach it.</param>
    private static Choice Unsatisfied(string id, IReadOnlyList<Dependency> reaching) =>
        new(null, null, Nuspec.Empty, $"{id}@{string.Join(" ", reaching.Select(dependency => dependency.Text).Distinct(StringComparer.Ordinal))}");

    /// <summary>The lowest cached version of a dependency every range reaching it admits, read once per id and ranges.</summary>
    /// <param name="id">The dependency id.</param>
    /// <param name="reaching">The dependencies on it that reach it.</param>
    /// <param name="major">The platform major.</param>
    /// <param name="choices">The choices already made in this closure, by id and ranges.</param>
    /// <param name="checks">The generation's checks; cancels before the chosen version's nuspec is read.</param>
    private Choice Choose(string id, IReadOnlyList<Dependency> reaching, int major, Dictionary<string, Choice> choices,
                          GenerationChecks checks)
    {
        var unsatisfied = Unsatisfied(id, reaching);
        if (choices.TryGetValue(unsatisfied.Missing!, out var known))
            return known;
        return choices[unsatisfied.Missing!] = Lowest(id, reaching, major, unsatisfied, checks);
    }

    private Choice Lowest(string id, IReadOnlyList<Dependency> reaching, int major, Choice unsatisfied, GenerationChecks checks)
    {
        var idDirectory = Path.GetFullPath(Path.Combine(packagesFolder, id.ToLowerInvariant()));
        if (!IsPackageId(id) || reaching.Any(dependency => dependency.Range is null) || !Contains(packagesFolder, idDirectory) ||
            !Directory.Exists(idDirectory))
            return unsatisfied;
        var lowest = Directory.EnumerateDirectories(idDirectory)
                              .Select(directory => (Directory: Path.GetFullPath(directory),
                                                    Version: PackageVersion.TryParse(Path.GetFileName(directory), out var parsed) ? parsed : null))
                              .Where(entry => entry.Version is not null && reaching.All(dependency => dependency.Range!.Admits(entry.Version)))
                              .OrderBy(entry => entry.Version)
                              .FirstOrDefault();
        if (lowest.Version is null)
            return unsatisfied;
        var version = lowest.Version.Normalized;
        return LibFolder(lowest.Directory, major) is { } lib
            ? new Choice(version, lib, ReadNuspec(lowest.Directory, major, checks), null)
            : new Choice(version, null, Nuspec.Empty, $"{id}@{version}: no compatible lib folder");
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
    /// <param name="Missing">The dependencies skipped, named.</param>
    /// <param name="FrameworkReferences">The shared frameworks the dependencies taken reference, each once.</param>
    private sealed record ClosedDependencies(IReadOnlyList<string> Folders, IReadOnlyList<string> Missing, IReadOnlyList<string> FrameworkReferences);

    /// <summary>What a nuspec lists for the platform.</summary>
    /// <param name="Dependencies">The dependencies of the chosen group.</param>
    /// <param name="FrameworkReferences">The names of the shared frameworks the chosen group references.</param>
    private sealed record Nuspec(IReadOnlyList<Dependency> Dependencies, IReadOnlyList<string> FrameworkReferences)
    {
        public static readonly Nuspec Empty = new([], []);
    }

    private sealed record Choice(string? Version, string? Lib, Nuspec Nuspec, string? Missing);
}

/// <summary>What the resolver reads of a managed assembly's metadata.</summary>
/// <param name="Name">The assembly name.</param>
/// <param name="Version">The assembly version.</param>
/// <param name="IsReferenceAssembly">Whether it carries <c>ReferenceAssemblyAttribute</c>.</param>
/// <param name="Mvid">The module version id.</param>
internal sealed record AssemblyFile(string Name, Version Version, bool IsReferenceAssembly, Guid Mvid)
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
            return new AssemblyFile(reader.GetString(definition.Name), definition.Version, isReference,
                                    reader.GetGuid(reader.GetModuleDefinition().Mvid));
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
