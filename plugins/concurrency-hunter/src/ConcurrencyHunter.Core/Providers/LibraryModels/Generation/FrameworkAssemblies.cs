using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The implementation assemblies of the two shared frameworks whose census ships as built-in models (TD-034a): the newest installed
/// patch of <c>Microsoft.NETCore.App</c> and <c>Microsoft.AspNetCore.App</c> of the census's major version, each assembly with the top-level
/// types it defines. <c>System.Private.CoreLib</c> is left out: its census is its own. Which built-in records are the census's
/// (<see cref="IsCensusEntry"/>) is decided here, for the run that regenerates the census and for the tool that writes it alike.</summary>
internal sealed class FrameworkAssemblies
{
    /// <summary>The major version of the frameworks the census describes.</summary>
    internal const int MAJOR = 10;

    /// <summary>The shared frameworks the census describes, in the order a framework's assemblies are looked up.</summary>
    internal static readonly IReadOnlyList<string> FRAMEWORKS = ["Microsoft.NETCore.App", "Microsoft.AspNetCore.App"];

    private const string CORELIB = "System.Private.CoreLib";

    /// <summary>Every framework assembly's version, at the census's major: an assembly of a shared framework keeps <c>major.0.0.0</c> across
    /// patches.</summary>
    private static readonly Version CensusVersion = new(MAJOR, 0, 0, 0);

    private readonly Dictionary<string, FrameworkAssembly> _assemblies;

    /// <summary>The assemblies, each with its framework and top-level types.</summary>
    /// <param name="assemblies">The assemblies by name.</param>
    /// <param name="patches">The installed patch of each framework.</param>
    /// <param name="files">Every managed assembly file of the frameworks, CoreLib's included.</param>
    /// <param name="referenceFiles">The reference assemblies of the frameworks' newest reference packs of the major.</param>
    private FrameworkAssemblies(Dictionary<string, FrameworkAssembly> assemblies, IReadOnlyDictionary<string, string> patches, IReadOnlyList<string> files,
                                IReadOnlyList<string> referenceFiles)
    {
        _assemblies = assemblies;
        Patches = patches;
        Files = files;
        ReferenceFiles = referenceFiles;
    }

    /// <summary>The reference assemblies of the frameworks' newest reference packs of the census's major: what a program's call of a member
    /// binds to, so what a census model must apply to.</summary>
    internal IReadOnlyList<string> ReferenceFiles { get; }

    /// <summary>The patch folder each framework's assemblies were read from, such as <c>10.0.14</c>.</summary>
    internal IReadOnlyDictionary<string, string> Patches { get; }

    /// <summary>Every managed assembly file of the frameworks, CoreLib's included, each assembly once: what a compilation that names the
    /// frameworks' members references.</summary>
    internal IReadOnlyList<string> Files { get; }

    /// <summary>The frameworks' assemblies as installed under a dotnet root: for each framework the newest patch folder of the census's major;
    /// in it every managed assembly but <c>System.Private.CoreLib</c>. An assembly both frameworks hold is taken from the first.</summary>
    /// <param name="dotnetRoot">The dotnet root whose <c>shared</c> folder holds the frameworks.</param>
    /// <exception cref="InvalidOperationException">A framework of the census's major is not installed.</exception>
    internal static FrameworkAssemblies Installed(string dotnetRoot)
    {
        var assemblies = new Dictionary<string, FrameworkAssembly>(StringComparer.Ordinal);
        var patches = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<string>();
        var references = new List<string>();
        foreach (var framework in FRAMEWORKS)
        {
            WorkCancellation.Check();
            var folder = Newest(Path.Combine(dotnetRoot, "shared", framework))
                         ?? throw new InvalidOperationException($"No {framework} {MAJOR}.0 is installed under {dotnetRoot}.");
            var pack = Newest(Path.Combine(dotnetRoot, "packs", framework + ".Ref")) is { } packFolder
                ? Path.Combine(packFolder, "ref", $"net{MAJOR}.0")
                : throw new InvalidOperationException($"No {framework}.Ref {MAJOR}.0 is installed under {dotnetRoot}.");
            references.AddRange(Directory.GetFiles(pack, "*.dll").Order(StringComparer.Ordinal));
            patches.Add(framework, Path.GetFileName(folder));
            foreach (var file in Directory.GetFiles(folder, "*.dll").Order(StringComparer.Ordinal))
            {
                WorkCancellation.Check();
                var name = Path.GetFileNameWithoutExtension(file);
                if (assemblies.ContainsKey(name) || TopLevelTypes(file) is not { } types)
                    continue;
                files.Add(file);
                if (name != CORELIB)
                    assemblies.Add(name, new FrameworkAssembly(framework, types));
            }
        }

        return new FrameworkAssemblies(assemblies, patches, files, references);
    }

    /// <summary>The newest version folder of the census's major in a folder of versions, such as <c>shared/Microsoft.NETCore.App</c>, or
    /// <c>null</c> when there is none.</summary>
    /// <param name="folder">The folder of versions.</param>
    private static string? Newest(string folder) =>
        (Directory.Exists(folder) ? Directory.GetDirectories(folder) : [])
            .Select(path => (Path: path, Version: Version.TryParse(Path.GetFileName(path), out var version) ? version : null))
            .Where(item => item.Version is { Major: MAJOR, Minor: 0 })
            .OrderByDescending(item => item.Version)
            .Select(item => item.Path)
            .FirstOrDefault();

    /// <summary>The framework an assembly belongs to, or <c>null</c> for one of neither framework.</summary>
    /// <param name="assemblyName">The assembly's name.</param>
    internal string? FrameworkOf(string assemblyName) => _assemblies.TryGetValue(assemblyName, out var assembly) ? assembly.Framework : null;

    /// <summary>Whether a built-in record is one of the census's: a record of a member that the run can generate, so the census run leaves it
    /// out and the census's regeneration replaces it. Every range of the record that holds at the census's major names an assembly of the
    /// two frameworks that defines the member's type itself; a record with none such, or with one that names CoreLib or a facade that
    /// forwards the type, as <c>System.Runtime</c> forwards <c>System.Array</c>, is not.</summary>
    /// <param name="record">The built-in record: a model or a failed member.</param>
    internal bool IsCensusEntry(LibraryModel record)
    {
        WorkCancellation.Check();
        var ranges = record.Assemblies.Where(range => range.Contains(CensusVersion)).ToArray();
        var type = LibraryModels.TypeOf(record.Id);
        return ranges.Length != 0 && ranges.All(range => Defines(range.AssemblyName, type));
    }

    /// <summary>Whether an assembly of the frameworks defines a type: the type itself, or the type it is nested in, is one of the assembly's
    /// top-level types. A declaration id writes a nested type after its outer type with a dot, as it writes a namespace, so every prefix of
    /// the name is tried, the longest first.</summary>
    /// <param name="assemblyName">The assembly's name.</param>
    /// <param name="type">The type's name as a declaration id writes it, without the prefix.</param>
    internal bool Defines(string assemblyName, string type)
    {
        if (!_assemblies.TryGetValue(assemblyName, out var assembly))
            return false;
        var name = type;
        while (true)
        {
            WorkCancellation.Check();
            if (assembly.Types.Contains(name))
                return true;
            var dot = name.LastIndexOf('.');
            if (dot < 0)
                return false;
            name = name[..dot];
        }
    }

    /// <summary>The full names of the top-level types a managed assembly defines, or <c>null</c> for a file with no metadata or no assembly.</summary>
    /// <param name="file">The file.</param>
    private static HashSet<string>? TopLevelTypes(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return null;
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
                return null;
            var types = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handle in reader.TypeDefinitions)
            {
                WorkCancellation.Check();
                var definition = reader.GetTypeDefinition(handle);
                if (!definition.GetDeclaringType().IsNil)
                    continue;
                var space = reader.GetString(definition.Namespace);
                var name = reader.GetString(definition.Name);
                types.Add(space.Length == 0 ? name : $"{space}.{name}");
            }

            return types;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>One assembly of the frameworks.</summary>
    /// <param name="Framework">The framework it belongs to.</param>
    /// <param name="Types">The full names of its top-level types.</param>
    private sealed record FrameworkAssembly(string Framework, HashSet<string> Types);
}
