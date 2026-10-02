using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

/// <summary>A fake dotnet root and NuGet global packages folder in a temporary folder, holding tiny emitted assemblies, for the
/// model generator's resolver and library compilation.</summary>
public sealed class GenerationInstall : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("ch-generation-").FullName;

    public string DotnetRoot => Path.Combine(Root, "dotnet");

    public string Packages => Path.Combine(Root, "packages");

    /// <summary>A resolver over this install.</summary>
    /// <param name="runtimeMajor">The platform major of a package member asked for without a framework.</param>
    public ImplementationAssemblies Resolver(int runtimeMajor = 8) => new(DotnetRoot, Packages, runtimeMajor);

    /// <summary>Creates <c>shared/Microsoft.NETCore.App/&lt;version&gt;</c> holding an emitted assembly per name.</summary>
    /// <param name="version">The patch folder.</param>
    /// <param name="assemblies">The assembly names, each emitted as a type-only library.</param>
    /// <param name="runtime">Also copies the running runtime's <c>System.Private.CoreLib</c> and <c>System.Runtime</c>, so that
    /// code can be compiled against the folder.</param>
    public string SharedFramework(string version, IEnumerable<string>? assemblies = null, bool runtime = false)
    {
        var directory = Directory.CreateDirectory(Path.Combine(DotnetRoot, "shared", "Microsoft.NETCore.App", version)).FullName;
        foreach (var assembly in assemblies ?? [])
            File.WriteAllBytes(Path.Combine(directory, assembly + ".dll"), EmittedAssemblies.Image(assembly));
        if (runtime)
        {
            foreach (var name in EmittedAssemblies.RUNTIME_ASSEMBLIES)
                File.Copy(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), name + ".dll"), Path.Combine(directory, name + ".dll"));
        }

        File.WriteAllText(Path.Combine(directory, "coreclr.dll"), "not a managed assembly");
        return directory;
    }

    /// <summary>Creates <c>&lt;packages&gt;/&lt;id&gt;/&lt;version&gt;</c> with a nuspec and emitted lib assemblies.</summary>
    /// <param name="id">The package id.</param>
    /// <param name="version">The version folder.</param>
    /// <param name="dependencies">The nuspec's <c>&lt;dependencies&gt;</c> element, or empty.</param>
    /// <param name="files">Lib files as <c>&lt;framework&gt;/&lt;assembly&gt;</c>, each emitted as a type-only library.</param>
    public string Package(string id, string version, string dependencies = "", params string[] files)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Packages, id.ToLowerInvariant(), version)).FullName;
        File.WriteAllText(Path.Combine(directory, id.ToLowerInvariant() + ".nuspec"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                {dependencies}
              </metadata>
            </package>
            """, new UTF8Encoding(false));
        foreach (var file in files)
        {
            var (framework, assembly) = (file.Split('/')[0], file.Split('/')[1]);
            var lib = Directory.CreateDirectory(Path.Combine(directory, "lib", framework)).FullName;
            File.WriteAllBytes(Path.Combine(lib, assembly + ".dll"), EmittedAssemblies.Image(assembly));
        }

        return directory;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Tiny assemblies emitted against the running runtime.</summary>
public static class EmittedAssemblies
{
    public static readonly string[] RUNTIME_ASSEMBLIES = ["System.Private.CoreLib", "System.Runtime"];

    private static readonly ConcurrentDictionary<(string Name, string Source, bool Reference), byte[]> Images = new();

    /// <summary>The running runtime's corelib and <c>System.Runtime</c>.</summary>
    public static IReadOnlyList<MetadataReference> RuntimeReferences { get; } =
        RUNTIME_ASSEMBLIES.Select(name => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), name + ".dll")))
                          .ToArray();

    /// <summary>An assembly image.</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="source">Its source; a single marker type when empty.</param>
    /// <param name="referenceAssembly">Whether it carries <c>ReferenceAssemblyAttribute</c>.</param>
    public static byte[] Image(string assemblyName, string source = "", bool referenceAssembly = false) =>
        Images.GetOrAdd((assemblyName, source, referenceAssembly), key =>
        {
            var text = key.Source.Length > 0 ? key.Source : $"public sealed class Marker_{key.Name.Replace('.', '_').Replace('-', '_')} {{ }}";
            if (key.Reference)
                text = "[assembly: System.Runtime.CompilerServices.ReferenceAssembly]\n" + text;
            var compilation = CSharpCompilation.Create(key.Name, [CSharpSyntaxTree.ParseText(text)], RuntimeReferences,
                                                       new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            return stream.ToArray();
        });

    /// <summary>Writes an assembly image to a file, creating its directory.</summary>
    /// <param name="path">The file.</param>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="source">Its source; a single marker type when empty.</param>
    /// <param name="referenceAssembly">Whether it carries <c>ReferenceAssemblyAttribute</c>.</param>
    public static string Write(string path, string assemblyName, string source = "", bool referenceAssembly = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Image(assemblyName, source, referenceAssembly));
        return path;
    }
}
