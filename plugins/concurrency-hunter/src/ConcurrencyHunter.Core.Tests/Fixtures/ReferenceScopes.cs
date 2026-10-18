using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

/// <summary>Compilations the way a loaded solution gives them: the runtime's references from a reference pack and a package's from the
/// packages folder, both named with <c>\</c> and <c>/</c> mixed in one path, over a fake install.</summary>
internal static class ReferenceScopes
{
    /// <summary>Gives an assembly the version <c>1.0.0.0</c>.</summary>
    public const string VERSION_ATTRIBUTE = "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")]\n";

    private static readonly Lazy<byte[]> SystemRuntime =
        new(() => File.ReadAllBytes(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.Runtime.dll")));

    /// <summary>A path, its segments joined by <c>/</c> and <c>\</c> in turn.</summary>
    /// <param name="root">The first part.</param>
    /// <param name="segments">The segments below it.</param>
    public static string Join(string root, params string[] segments)
    {
        var path = new StringBuilder(root);
        for (var i = 0; i < segments.Length; i++)
            path.Append(i % 2 == 0 ? '/' : '\\').Append(segments[i]);
        return path.ToString();
    }

    /// <summary>A metadata reference to an image under a path the file need not have.</summary>
    /// <param name="image">The assembly image.</param>
    /// <param name="path">The path the reference reports.</param>
    public static MetadataReference Reference(byte[] image, string path) => MetadataReference.CreateFromImage(image, filePath: path);

    /// <summary>The path of a file in a reference pack: <c>&lt;dotnet&gt;/packs/&lt;pack&gt;/&lt;major&gt;.0.5/ref/net&lt;major&gt;.0/&lt;file&gt;</c>.</summary>
    /// <param name="install">The install whose dotnet root holds the pack.</param>
    /// <param name="major">The major of the <c>net&lt;major&gt;.0</c> folder.</param>
    /// <param name="pack">The reference pack's name, <c>Microsoft.NETCore.App.Ref</c> or <c>Microsoft.AspNetCore.App.Ref</c>.</param>
    /// <param name="file">The file name.</param>
    public static string PackFile(GenerationInstall install, int major, string pack, string file) =>
        Join(install.DotnetRoot, "packs", pack, $"{major}.0.5", "ref", $"net{major}.0", file);

    /// <summary>The references every scope needs to compile: the runtime's corelib and <c>System.Runtime</c>, the latter from the
    /// <c>Microsoft.NETCore.App.Ref</c> pack of a major or, without one, from the runtime's folder.</summary>
    /// <param name="install">The install whose dotnet root holds the pack.</param>
    /// <param name="packMajor">The pack's major, or <c>null</c> for a compilation that references no pack.</param>
    public static IReadOnlyList<MetadataReference> Runtime(GenerationInstall install, int? packMajor) =>
    [
        EmittedAssemblies.RuntimeReferences[0],
        packMajor is { } major ? Reference(SystemRuntime.Value, PackFile(install, major, "Microsoft.NETCore.App.Ref", "System.Runtime.dll"))
                               : EmittedAssemblies.RuntimeReferences[1]
    ];

    /// <summary>The path a package asset is referenced by: <c>&lt;packages&gt;/&lt;id in lower case&gt;/&lt;version&gt;/&lt;folder&gt;/&lt;framework&gt;/&lt;assembly&gt;.dll</c>.</summary>
    /// <param name="install">The install whose packages folder it is in.</param>
    /// <param name="id">The package id.</param>
    /// <param name="version">The package version folder.</param>
    /// <param name="folder">The folder below the version: <c>lib</c>, <c>ref</c> or <c>runtimes/win/lib</c>.</param>
    /// <param name="framework">The framework folder below it.</param>
    /// <param name="assembly">The assembly name.</param>
    public static string PackagePath(GenerationInstall install, string id, string version, string folder, string framework, string assembly) =>
        Join(install.Packages, [id.ToLowerInvariant(), version, .. folder.Split('/'), framework, assembly + ".dll"]);

    /// <summary>Writes an asset into a package's <c>lib</c> folder, with the package's nuspec.</summary>
    /// <param name="install">The install.</param>
    /// <param name="id">The package id.</param>
    /// <param name="version">The package version.</param>
    /// <param name="framework">The framework folder below <c>lib</c>.</param>
    /// <param name="assembly">The assembly name.</param>
    /// <param name="image">The assembly image.</param>
    /// <param name="nuspecMetadata">Extra elements of the nuspec's <c>metadata</c>.</param>
    public static string AddAsset(GenerationInstall install, string id, string version, string framework, string assembly, byte[] image,
                                  string nuspecMetadata = "")
    {
        var path = Path.Combine(install.Package(id, version, nuspecMetadata), "lib", framework, assembly + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, image);
        return path;
    }

    /// <summary>A scope compilation over references.</summary>
    /// <param name="source">The scope's source.</param>
    /// <param name="references">The references.</param>
    public static CSharpCompilation Compile(string source, IEnumerable<MetadataReference> references) =>
        CSharpCompilation.Create("App", [CSharpSyntaxTree.ParseText(source)], references,
                                 new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
}
