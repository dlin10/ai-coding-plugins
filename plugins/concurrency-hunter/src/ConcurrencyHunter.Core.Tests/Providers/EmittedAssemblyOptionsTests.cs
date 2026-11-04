using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using Xunit.Sdk;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The fixture's emitted assemblies take a version, a file version, a public key, references to other emitted assemblies and
/// suppressed diagnostic ids, so that a test can lay a library with the identity and references a package or a framework gives it.</summary>
public sealed class EmittedAssemblyOptionsTests : IDisposable
{
    private readonly GenerationInstall _install = new();

    public void Dispose() => _install.Dispose();

    [Fact]
    public void An_emitted_image_carries_the_assembly_version_it_is_given()
    {
        var plain = Read(EmittedAssemblies.Image("Fixture.Versioned"));
        var versioned = Read(EmittedAssemblies.Image("Fixture.Versioned", version: "3.1.4.1"));

        Assert.Equal(new Version(0, 0, 0, 0), plain.Version);
        Assert.Equal(new Version(3, 1, 4, 1), versioned.Version);
    }

    [Fact]
    public void An_emitted_image_carries_the_file_version_it_is_given_apart_from_the_assembly_version()
    {
        var plain = EmittedAssemblies.Write(Path.Combine(_install.Root, "plain", "Fixture.File.dll"), "Fixture.File");
        var versioned = EmittedAssemblies.Write(Path.Combine(_install.Root, "versioned", "Fixture.File.dll"), "Fixture.File", version: "5.0.0.0",
                                                fileVersion: "5.0.100.2");

        Assert.Equal("5.0.100.2", FileVersionInfo.GetVersionInfo(versioned).FileVersion);
        Assert.Equal(new Version(5, 0, 0, 0), AssemblyName.GetAssemblyName(versioned).Version);
        Assert.NotEqual("5.0.100.2", FileVersionInfo.GetVersionInfo(plain).FileVersion);
    }

    [Fact]
    public void An_emitted_image_carries_the_public_key_it_is_given()
    {
        var signed = Read(EmittedAssemblies.Image("Fixture.Signed", publicKey: EmittedAssemblies.TestKey));
        var unsigned = Read(EmittedAssemblies.Image("Fixture.Signed"));

        Assert.NotEmpty(EmittedAssemblies.TestKey);
        Assert.Equal(EmittedAssemblies.TestKey, signed.PublicKey);
        Assert.True(signed.Flags.HasFlag(AssemblyFlags.PublicKey));
        Assert.Empty(unsigned.PublicKey);
        Assert.False(unsigned.Flags.HasFlag(AssemblyFlags.PublicKey));
    }

    [Fact]
    public void An_emitted_image_names_each_referenced_image_with_the_assembly_version_of_that_image()
    {
        var dep = EmittedAssemblies.Image("Fixture.Dep", "namespace Fixture.Dep { public static class Util { } }", version: "4.2.0.0");
        var other = EmittedAssemblies.Image("Fixture.Other", version: "7.0.0.0", publicKey: EmittedAssemblies.TestKey);

        var both = Read(EmittedAssemblies.Image("Fixture.Lib", references: [dep, other])).References;
        var onlyDep = Read(EmittedAssemblies.Image("Fixture.Lib", references: [dep])).References;
        var none = Read(EmittedAssemblies.Image("Fixture.Lib")).References;

        Assert.Equal(new Version(4, 2, 0, 0), Assert.Single(both, reference => reference.Name == "Fixture.Dep").Version);
        Assert.Equal(new Version(7, 0, 0, 0), Assert.Single(both, reference => reference.Name == "Fixture.Other").Version);
        Assert.Equal(new Version(4, 2, 0, 0), Assert.Single(onlyDep, reference => reference.Name == "Fixture.Dep").Version);
        Assert.DoesNotContain(onlyDep, reference => reference.Name == "Fixture.Other");
        Assert.DoesNotContain(none, reference => reference.Name is "Fixture.Dep" or "Fixture.Other");
    }

    [Fact]
    public void An_emitted_image_suppresses_the_diagnostic_ids_it_is_given()
    {
        var experimental = EmittedAssemblies.Image("Fixture.Experimental", """
            using System.Diagnostics.CodeAnalysis;
            namespace Fixture.Experimental
            {
                public static class Stable { }
                [Experimental("ASP0029")] public static class Unstable { public static void Run() { } }
            }
            """);
        const string USER = "public static class User { public static void Call() => Fixture.Experimental.Unstable.Run(); }";

        var failure = Assert.ThrowsAny<XunitException>(() => EmittedAssemblies.Image("Fixture.User", USER, references: [experimental]));
        var image = EmittedAssemblies.Image("Fixture.User", USER, references: [experimental], suppressedDiagnostics: ["ASP0029"]);

        Assert.Contains("ASP0029", failure.Message);
        Assert.Contains(Read(image).References, reference => reference.Name == "Fixture.Experimental");
    }

    [Fact]
    public void An_emitted_image_is_cached_for_the_options_it_was_asked_with_and_only_those()
    {
        var dep = EmittedAssemblies.Image("Fixture.CacheDep");
        var otherDep = EmittedAssemblies.Image("Fixture.CacheOtherDep");
        var asked = Ask(dep);

        Assert.Same(asked, Ask(dep));
        Assert.NotSame(asked, Ask(dep, version: "1.2.3.5"));
        Assert.NotSame(asked, Ask(dep, fileVersion: "1.2.3.6"));
        Assert.NotSame(asked, Ask(dep, signed: false));
        Assert.NotSame(asked, Ask(otherDep));
        Assert.NotSame(asked, Ask(dep, suppressed: "ASP0030"));
        Assert.NotSame(asked, EmittedAssemblies.Image("Fixture.Cache"));
    }

    [Fact]
    public void A_generation_install_lays_a_given_image_instead_of_the_type_only_one_and_beside_the_listed_names()
    {
        var laid = EmittedAssemblies.Image("Fixture.Laid", version: "6.0.0.0");
        var images = new Dictionary<string, byte[]> { ["Fixture.Laid"] = laid };

        var shared = _install.SharedFramework("8.0.1", ["Fixture.Plain", "Fixture.Laid"], images: images);
        var aspNet = _install.AspNetCoreFramework("8.0.1", ["Fixture.Plain"], images);
        var package = _install.Package("Fixture.Pack", "1.0.0", "", new Dictionary<string, byte[]> { ["net8.0/Fixture.Laid"] = laid },
                                       "net8.0/Fixture.Plain");

        Assert.Equal(laid, File.ReadAllBytes(Path.Combine(shared, "Fixture.Laid.dll")));
        Assert.Equal(laid, File.ReadAllBytes(Path.Combine(aspNet, "Fixture.Laid.dll")));
        Assert.Equal(laid, File.ReadAllBytes(Path.Combine(package, "lib", "net8.0", "Fixture.Laid.dll")));
        Assert.Equal(EmittedAssemblies.Image("Fixture.Plain"), File.ReadAllBytes(Path.Combine(shared, "Fixture.Plain.dll")));
        Assert.Equal(EmittedAssemblies.Image("Fixture.Plain"), File.ReadAllBytes(Path.Combine(aspNet, "Fixture.Plain.dll")));
        Assert.Equal(EmittedAssemblies.Image("Fixture.Plain"), File.ReadAllBytes(Path.Combine(package, "lib", "net8.0", "Fixture.Plain.dll")));
    }

    /// <summary>An image of <c>Fixture.Cache</c> with every option given.</summary>
    /// <param name="reference">The one image it references.</param>
    /// <param name="version">Its assembly version.</param>
    /// <param name="fileVersion">Its file version.</param>
    /// <param name="signed">Whether it is compiled with the test key.</param>
    /// <param name="suppressed">The one diagnostic id it suppresses.</param>
    private static byte[] Ask(byte[] reference, string version = "1.2.3.4", string fileVersion = "1.2.3.5", bool signed = true,
                              string suppressed = "ASP0029") =>
        EmittedAssemblies.Image("Fixture.Cache", version: version, fileVersion: fileVersion, publicKey: signed ? EmittedAssemblies.TestKey : null,
                                references: [reference], suppressedDiagnostics: [suppressed]);

    /// <summary>The identity and references an image's metadata carries.</summary>
    /// <param name="image">The assembly image.</param>
    private static (Version Version, byte[] PublicKey, AssemblyFlags Flags, (string Name, Version Version)[] References) Read(byte[] image)
    {
        using var peReader = new PEReader(ImmutableArray.Create(image));
        var metadata = peReader.GetMetadataReader();
        var definition = metadata.GetAssemblyDefinition();
        var references = metadata.AssemblyReferences.Select(handle => metadata.GetAssemblyReference(handle))
                                 .Select(reference => (metadata.GetString(reference.Name), reference.Version)).ToArray();
        return (definition.Version, metadata.GetBlobBytes(definition.PublicKey), definition.Flags, references);
    }
}
