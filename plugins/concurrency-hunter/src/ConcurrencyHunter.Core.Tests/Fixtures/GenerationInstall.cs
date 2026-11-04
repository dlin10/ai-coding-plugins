using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    /// <param name="nuspecRead">Told the path of each nuspec the resolver reads.</param>
    public ImplementationAssemblies Resolver(int runtimeMajor = 8, Action<string>? nuspecRead = null) =>
        new(DotnetRoot, Packages, runtimeMajor) { NuspecRead = nuspecRead };

    /// <summary>Creates <c>shared/Microsoft.NETCore.App/&lt;version&gt;</c> holding an emitted assembly per name.</summary>
    /// <param name="version">The patch folder.</param>
    /// <param name="assemblies">The assembly names, each emitted as a type-only library.</param>
    /// <param name="runtime">Also copies the running runtime's <c>System.Private.CoreLib</c> and <c>System.Runtime</c>, so that
    /// code can be compiled against the folder.</param>
    /// <param name="images">Assembly images by assembly name, laid instead of the type-only emission of the same name, or beside
    /// <paramref name="assemblies"/> when the name is not listed there.</param>
    public string SharedFramework(string version, IEnumerable<string>? assemblies = null, bool runtime = false,
                                  IReadOnlyDictionary<string, byte[]>? images = null)
    {
        var directory = Framework("Microsoft.NETCore.App", version, assemblies, images);
        if (runtime)
        {
            foreach (var name in EmittedAssemblies.RUNTIME_ASSEMBLIES)
                File.Copy(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), name + ".dll"), Path.Combine(directory, name + ".dll"));
        }

        File.WriteAllText(Path.Combine(directory, "coreclr.dll"), "not a managed assembly");
        return directory;
    }

    /// <summary>Creates <c>shared/Microsoft.AspNetCore.App/&lt;version&gt;</c> holding an emitted assembly per name.</summary>
    /// <param name="version">The patch folder.</param>
    /// <param name="assemblies">The assembly names, each emitted as a type-only library.</param>
    /// <param name="images">Assembly images by assembly name, laid instead of the type-only emission of the same name, or beside
    /// <paramref name="assemblies"/> when the name is not listed there.</param>
    public string AspNetCoreFramework(string version, IEnumerable<string>? assemblies = null, IReadOnlyDictionary<string, byte[]>? images = null)
    {
        var directory = Framework("Microsoft.AspNetCore.App", version, assemblies, images);
        File.WriteAllText(Path.Combine(directory, "aspnetcorev2_inprocess.dll"), "not a managed assembly");
        return directory;
    }

    /// <summary>A nuspec <c>&lt;frameworkReferences&gt;</c> element with one group, to pass as a package's nuspec metadata.</summary>
    /// <param name="targetFramework">The group's target framework, <c>net8.0</c>.</param>
    /// <param name="names">The shared frameworks the group references.</param>
    public static string FrameworkReferences(string targetFramework, params string[] names) =>
        $"<frameworkReferences><group targetFramework='{targetFramework}'>" +
        string.Concat(names.Select(name => $"<frameworkReference name='{name}' />")) +
        "</group></frameworkReferences>";

    /// <summary>Creates <c>&lt;packages&gt;/&lt;id&gt;/&lt;version&gt;</c> with a nuspec and emitted lib assemblies.</summary>
    /// <param name="id">The package id.</param>
    /// <param name="version">The version folder.</param>
    /// <param name="dependencies">The nuspec's <c>&lt;dependencies&gt;</c> element, or empty.</param>
    /// <param name="files">Lib files as <c>&lt;framework&gt;/&lt;assembly&gt;</c>, each emitted as a type-only library.</param>
    public string Package(string id, string version, string dependencies = "", params string[] files) =>
        Package(id, version, dependencies, new Dictionary<string, byte[]>(), files);

    /// <summary>Creates <c>&lt;packages&gt;/&lt;id&gt;/&lt;version&gt;</c> with a nuspec, emitted lib assemblies and given lib images.</summary>
    /// <param name="id">The package id.</param>
    /// <param name="version">The version folder.</param>
    /// <param name="dependencies">The nuspec's <c>&lt;dependencies&gt;</c> element, or empty.</param>
    /// <param name="images">Assembly images by <c>&lt;framework&gt;/&lt;assembly&gt;</c>, laid instead of the type-only emission of the
    /// same file, or beside <paramref name="files"/> when the file is not listed there.</param>
    /// <param name="files">Lib files as <c>&lt;framework&gt;/&lt;assembly&gt;</c>, each emitted as a type-only library.</param>
    public string Package(string id, string version, string dependencies, IReadOnlyDictionary<string, byte[]> images, params string[] files)
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
        foreach (var file in files.Concat(images.Keys).Distinct())
        {
            var (framework, assembly) = (file.Split('/')[0], file.Split('/')[1]);
            var lib = Directory.CreateDirectory(Path.Combine(directory, "lib", framework)).FullName;
            File.WriteAllBytes(Path.Combine(lib, assembly + ".dll"), images.TryGetValue(file, out var image) ? image : EmittedAssemblies.Image(assembly));
        }

        return directory;
    }

    /// <summary>Creates <c>shared/&lt;framework&gt;/&lt;version&gt;</c> holding an emitted assembly per name.</summary>
    /// <param name="framework">The shared framework's name.</param>
    /// <param name="version">The patch folder.</param>
    /// <param name="assemblies">The assembly names, each emitted as a type-only library.</param>
    /// <param name="images">Assembly images by assembly name, laid instead of the type-only emission of the same name, or beside
    /// <paramref name="assemblies"/> when the name is not listed there.</param>
    private string Framework(string framework, string version, IEnumerable<string>? assemblies, IReadOnlyDictionary<string, byte[]>? images)
    {
        var directory = Directory.CreateDirectory(Path.Combine(DotnetRoot, "shared", framework, version)).FullName;
        foreach (var assembly in (assemblies ?? []).Concat(images?.Keys ?? []).Distinct())
            File.WriteAllBytes(Path.Combine(directory, assembly + ".dll"), images is not null && images.TryGetValue(assembly, out var image) ? image : EmittedAssemblies.Image(assembly));
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

    private static readonly ConcurrentDictionary<(string Name, string Source, bool Reference, string? Version, string? FileVersion,
                                                  string PublicKey, string References, string Suppressed), byte[]> Images = new();

    /// <summary>The running runtime's corelib and <c>System.Runtime</c>.</summary>
    public static IReadOnlyList<MetadataReference> RuntimeReferences { get; } =
        RUNTIME_ASSEMBLIES.Select(name => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), name + ".dll")))
                          .ToArray();

    /// <summary>A public key to sign an image with: the running runtime's <c>System.Runtime</c> public key, read from its metadata.
    /// Public signing needs no private key.</summary>
    public static byte[] TestKey { get; } =
        AssemblyName.GetAssemblyName(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.Runtime.dll")).GetPublicKey()!;

    /// <summary>An assembly image.</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="source">Its source; a single marker type when empty. It must not carry an <c>AssemblyVersion</c> or
    /// <c>AssemblyFileVersion</c> when <paramref name="version"/> or <paramref name="fileVersion"/> is given.</param>
    /// <param name="referenceAssembly">Whether it carries <c>ReferenceAssemblyAttribute</c>.</param>
    /// <param name="version">Its assembly version, or <c>null</c> for the compiler's <c>0.0.0.0</c>.</param>
    /// <param name="fileVersion">Its file version, written as the version resource that <c>FileVersionInfo</c> reads, or <c>null</c>
    /// for an image without that resource.</param>
    /// <param name="publicKey">Its public key, compiled with public signing, or <c>null</c> for an unsigned image.</param>
    /// <param name="references">Images of other emitted assemblies it references: it names one public type of each, so that it
    /// carries an AssemblyRef row with the version of that assembly.</param>
    /// <param name="suppressedDiagnostics">Diagnostic ids the compilation suppresses, such as an <c>[Experimental]</c> id.</param>
    public static byte[] Image(string assemblyName, string source = "", bool referenceAssembly = false, string? version = null,
                               string? fileVersion = null, byte[]? publicKey = null, IReadOnlyList<byte[]>? references = null,
                               IReadOnlyList<string>? suppressedDiagnostics = null) =>
        Images.GetOrAdd((assemblyName, source, referenceAssembly, version, fileVersion, Convert.ToHexString(publicKey ?? []),
                         string.Join(',', (references ?? []).Select(image => Convert.ToHexString(SHA256.HashData(image)))),
                         string.Join(',', suppressedDiagnostics ?? [])), key =>
        {
            var text = source.Length > 0 ? source : $"public sealed class Marker_{assemblyName.Replace('.', '_').Replace('-', '_')} {{ }}";
            if (referenceAssembly)
                text = "[assembly: System.Runtime.CompilerServices.ReferenceAssembly]\n" + text;
            var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(text) };
            var attributes = new StringBuilder();
            if (version is not null)
                attributes.AppendLine($"[assembly: System.Reflection.AssemblyVersion(\"{version}\")]");
            if (fileVersion is not null)
                attributes.AppendLine($"[assembly: System.Reflection.AssemblyFileVersion(\"{fileVersion}\")]");
            if (attributes.Length > 0)
                trees.Add(CSharpSyntaxTree.ParseText(attributes.ToString()));
            var referenced = (references ?? []).Select(image => (MetadataReference)MetadataReference.CreateFromImage(image)).ToArray();
            if (referenced.Length > 0)
                trees.Add(CSharpSyntaxTree.ParseText(ReferenceAnchors(referenced)));
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true);
            if (publicKey is { Length: > 0 })
                options = options.WithCryptoPublicKey([.. publicKey]).WithPublicSign(true);
            if (suppressedDiagnostics is { Count: > 0 })
                options = options.WithSpecificDiagnosticOptions(suppressedDiagnostics.Distinct().ToDictionary(id => id, _ => ReportDiagnostic.Suppress));
            var compilation = CSharpCompilation.Create(assemblyName, trees, RuntimeReferences.Concat(referenced), options);
            using var stream = new MemoryStream();
            using var versionResource = fileVersion is null ? null : compilation.CreateDefaultWin32Resources(versionResource: true, noManifest: true,
                                                                                                             manifestContents: null, iconInIcoFormat: null);
            var emitted = compilation.Emit(stream, win32Resources: versionResource);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            return stream.ToArray();
        });

    /// <summary>Writes an assembly image to a file, creating its directory.</summary>
    /// <param name="path">The file.</param>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="source">Its source; a single marker type when empty.</param>
    /// <param name="referenceAssembly">Whether it carries <c>ReferenceAssemblyAttribute</c>.</param>
    /// <param name="version">Its assembly version, or <c>null</c> for the compiler's <c>0.0.0.0</c>.</param>
    /// <param name="fileVersion">Its file version, written as the version resource that <c>FileVersionInfo</c> reads, or <c>null</c>
    /// for an image without that resource.</param>
    /// <param name="publicKey">Its public key, compiled with public signing, or <c>null</c> for an unsigned image.</param>
    /// <param name="references">Images of other emitted assemblies it references, each named by one public type.</param>
    /// <param name="suppressedDiagnostics">Diagnostic ids the compilation suppresses, such as an <c>[Experimental]</c> id.</param>
    public static string Write(string path, string assemblyName, string source = "", bool referenceAssembly = false, string? version = null,
                               string? fileVersion = null, byte[]? publicKey = null, IReadOnlyList<byte[]>? references = null,
                               IReadOnlyList<string>? suppressedDiagnostics = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Image(assemblyName, source, referenceAssembly, version, fileVersion, publicKey, references, suppressedDiagnostics));
        return path;
    }

    /// <summary>The source of a class that names one public type of each referenced assembly: the compiler writes an AssemblyRef row
    /// only for an assembly the code uses, and the row is what carries the referenced version.</summary>
    /// <param name="referenced">The references, each an emitted assembly with a public type.</param>
    private static string ReferenceAnchors(IReadOnlyList<MetadataReference> referenced)
    {
        var probe = CSharpCompilation.Create("ReferenceAnchorsProbe", references: RuntimeReferences.Concat(referenced));
        var names = referenced.Select(reference => PublicTypes(((IAssemblySymbol)probe.GetAssemblyOrModuleSymbol(reference)!).GlobalNamespace)
                                          .Select(type => type.ToDisplayString()).Order(StringComparer.Ordinal).FirstOrDefault()
                                      ?? throw new InvalidOperationException("A referenced emitted assembly has no public type to name."));
        return "internal static class EmittedReferenceAnchors { internal static readonly System.Type[] Types = new System.Type[] { " +
               string.Join(", ", names.Select(name => $"typeof(global::{name})")) + " }; }";
    }

    /// <summary>The public non-generic types of a namespace and of the namespaces below it.</summary>
    /// <param name="scope">The namespace.</param>
    private static IEnumerable<INamedTypeSymbol> PublicTypes(INamespaceSymbol scope) =>
        scope.GetTypeMembers().Where(type => type.DeclaredAccessibility == Accessibility.Public && type.Arity == 0)
             .Concat(scope.GetNamespaceMembers().SelectMany(PublicTypes));
}
