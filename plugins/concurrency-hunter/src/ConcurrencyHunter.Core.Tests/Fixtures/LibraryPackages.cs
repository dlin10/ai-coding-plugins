using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

/// <summary>The real packages of the library table's families and their dependencies, as the build copied them next to the tests.
/// Only the tests of the table and its effects reference them.</summary>
public static class LibraryPackages
{
    private static readonly (string Name, MetadataReference Reference)[] Packages =
        Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
                 .Select(path => (Name: Path.GetFileNameWithoutExtension(path), Path: path))
                 .Where(file => file.Name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) ||
                                file.Name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
                                file.Name == "Newtonsoft.Json")
                 .Select(file => (file.Name, (MetadataReference)MetadataReference.CreateFromFile(file.Path)))
                 .ToArray();

    /// <summary>Every package, for a compilation over the runtime alone.</summary>
    public static IReadOnlyList<MetadataReference> All { get; } = Packages.Select(package => package.Reference).ToArray();

    /// <summary>The packages without the assemblies a stub stands for, so a fixture project that references the stubs never holds
    /// two assemblies of one name.</summary>
    public static IReadOnlyList<MetadataReference> BesideStubs { get; } =
        Packages.Where(package => !StubAssemblies.Names.Contains(package.Name, StringComparer.Ordinal))
                .Select(package => package.Reference)
                .ToArray();
}
