using System.Collections.Immutable;
using System.Runtime.InteropServices;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The copy of a strong-named library is compiled with the library's own identity: public-signed with its public key, which
/// the driver of its member carries too, and which the copy's <c>InternalsVisibleTo</c> names the driver with; a library without a key
/// is compiled, and names the driver, as before (SPEC TD-034b, «Идентичность»).</summary>
public sealed class LibraryIdentityTests : IDisposable
{
    private const string LIBRARY = "Fixture.Library";
    private const string FRIENDLY = "Fixture.Friendly";
    private const string SCHEDULE = "M:Lib.Api.Schedule(Lib.Scheduler,System.Action)";

    private const string SOURCE = """
        namespace Lib
        {
            public abstract class Scheduler
            {
                protected Scheduler() { }
                protected internal abstract void Queue(object item);
            }

            public static class Api
            {
                public static void Schedule(Scheduler scheduler, System.Action done) { scheduler.Queue(done); }
            }
        }
        """;

    private const string USES_FRIEND = """
        namespace Lib
        {
            public static class Api
            {
                internal static Fixture.Friendly.Secret Hold(Fixture.Friendly.Secret secret) { return secret; }
                public static void Run(System.Action done) { var kept = Hold(new Fixture.Friendly.Secret()); done(); }
            }
        }
        """;

    private static readonly ImmutableArray<byte> Key = [.. EmittedAssemblies.TestKey];
    private static readonly string KeyHex = Convert.ToHexStringLower(EmittedAssemblies.TestKey);

    private readonly GenerationInstall _install = new();

    public void Dispose() => _install.Dispose();

    [Fact]
    public void A_copy_compiled_with_a_key_carries_the_key_and_is_public_signed()
    {
        var copy = Compiled(SOURCE, signed: true);

        Assert.Equal(EmittedAssemblies.TestKey, copy.Options.CryptoPublicKey.ToArray());
        Assert.True(copy.Options.PublicSign);
        Assert.Equal(EmittedAssemblies.TestKey, copy.Assembly.Identity.PublicKey.ToArray());
    }

    [Fact]
    public void A_copy_compiled_without_a_key_is_not_signed()
    {
        var plain = Compiled(SOURCE, signed: false);
        var empty = LibraryCompilation.CompileTrees(LIBRARY, Trees(SOURCE), EmittedAssemblies.RuntimeReferences, CancellationToken.None,
                                                    publicKey: ImmutableArray<byte>.Empty).Compilation!;

        Assert.True(plain.Options.CryptoPublicKey.IsDefaultOrEmpty);
        Assert.False(plain.Options.PublicSign);
        Assert.False(plain.Assembly.Identity.HasPublicKey);
        Assert.True(empty.Options.CryptoPublicKey.IsDefaultOrEmpty);
        Assert.False(empty.Options.PublicSign);
    }

    [Fact]
    public void Decompile_reads_the_public_key_of_a_strong_named_assembly_and_leaves_an_unsigned_ones_empty()
    {
        var signed = LibraryCompilation.Decompile(Lay(LIBRARY, SOURCE, signed: true), CancellationToken.None);
        var plain = LibraryCompilation.Decompile(Lay(LIBRARY, SOURCE, signed: false), CancellationToken.None);

        Assert.Equal(EmittedAssemblies.TestKey, signed.PublicKey.ToArray());
        Assert.True(plain.PublicKey.IsEmpty);
    }

    [Fact]
    public void The_key_reaches_the_copy_through_Decompile_and_CompileModule()
    {
        var signed = Recompiled(Lay(LIBRARY, SOURCE, signed: true));
        var plain = Recompiled(Lay(LIBRARY, SOURCE, signed: false));

        Assert.True(signed.Reason is null && plain.Reason is null, string.Join(", ", signed.Errors.Concat(plain.Errors)));
        Assert.Equal(EmittedAssemblies.TestKey, signed.Compilation!.Options.CryptoPublicKey.ToArray());
        Assert.True(signed.Compilation.Options.PublicSign);
        Assert.True(plain.Compilation!.Options.CryptoPublicKey.IsDefaultOrEmpty);
        Assert.False(plain.Compilation.Options.PublicSign);
    }

    [Fact]
    public void The_key_reaches_the_copy_of_the_route_a_generation_takes()
    {
        var signed = LibraryCompilation.Compile(Lay(LIBRARY, SOURCE, signed: true), CancellationToken.None);
        var plain = LibraryCompilation.Compile(Lay(LIBRARY, SOURCE, signed: false), CancellationToken.None);

        Assert.True(signed.Reason is null && plain.Reason is null, string.Join(", ", signed.Errors.Concat(plain.Errors)));
        Assert.Equal(EmittedAssemblies.TestKey, signed.Compilation!.Options.CryptoPublicKey.ToArray());
        Assert.True(signed.Compilation.Options.PublicSign);
        Assert.Equal([DriverFriend(signed: true)], Friends(signed.Compilation));
        Assert.True(plain.Compilation!.Options.CryptoPublicKey.IsDefaultOrEmpty);
        Assert.Equal([DriverFriend(signed: false)], Friends(plain.Compilation));
    }

    [Fact]
    public void An_ordinary_opened_copy_names_the_driver_with_the_key_of_a_signed_library_and_bare_otherwise()
    {
        Assert.Equal([$"{DriverSynthesizer.ASSEMBLY}, PublicKey={KeyHex}"], Friends(Compiled(SOURCE, signed: true)));
        Assert.Equal([DriverSynthesizer.ASSEMBLY], Friends(Compiled(SOURCE, signed: false)));
    }

    [Fact]
    public void A_recipe_copy_names_the_driver_with_the_key_of_a_signed_library_and_bare_otherwise()
    {
        var signed = Compile(SOURCE, signed: true);
        var plain = Compile(SOURCE, signed: false);

        var signedRecipe = LibraryCompilation.RecipeCopy(signed, new GenerationChecks(CancellationToken.None)).Compilation!;
        var plainRecipe = LibraryCompilation.RecipeCopy(plain, new GenerationChecks(CancellationToken.None)).Compilation!;

        Assert.NotSame(signed.Compilation, signedRecipe);
        Assert.Equal(EmittedAssemblies.TestKey, signedRecipe.Options.CryptoPublicKey.ToArray());
        Assert.True(signedRecipe.Options.PublicSign);
        Assert.Equal([DriverFriend(signed: true)], Friends(signedRecipe));
        Assert.True(plainRecipe.Options.CryptoPublicKey.IsDefaultOrEmpty);
        Assert.Equal([DriverFriend(signed: false)], Friends(plainRecipe));
    }

    [Fact]
    public void A_framework_assembly_that_grants_its_internals_to_the_signed_library_compiles_with_the_key_and_not_without_it()
    {
        var references = EmittedAssemblies.RuntimeReferences
                                          .Append(MetadataReference.CreateFromImage(EmittedAssemblies.Image(FRIENDLY, FriendlySource()))).ToArray();

        var signed = Compile(USES_FRIEND, signed: true, references: references);
        var plain = Compile(USES_FRIEND, signed: false, references: references);

        Assert.True(signed.Reason is null, string.Join(", ", signed.Errors));
        Assert.Empty(signed.Errors);
        Assert.Equal(GenerationReasons.LIBRARY_DOES_NOT_COMPILE, plain.Reason);
        Assert.Contains(plain.Errors, error => error is "CS0122" or "CS0281");
    }

    [Fact]
    public void A_signed_library_that_uses_the_internals_a_framework_assembly_grants_it_compiles_whole_after_the_decompiler()
    {
        var friendly = EmittedAssemblies.Image(FRIENDLY, FriendlySource());
        var friendlyPath = EmittedAssemblies.Write(Path.Combine(_install.Root, "friendly", FRIENDLY + ".dll"), FRIENDLY, FriendlySource());
        var signed = Lay(LIBRARY, USES_FRIEND, signed: true, [friendly], [friendlyPath]);

        var module = LibraryCompilation.Decompile(signed, CancellationToken.None);
        var whole = Recompiled(signed);
        var withoutKey = LibraryCompilation.CompileModule(LIBRARY, module with { PublicKey = ImmutableArray<byte>.Empty },
                                                          signed.References.Select(reference => (MetadataReference)MetadataReference.CreateFromFile(reference)).ToArray(),
                                                          CancellationToken.None);

        Assert.Equal(EmittedAssemblies.TestKey, module.PublicKey.ToArray());
        Assert.True(whole.Reason is null, string.Join(", ", whole.Errors));
        Assert.Equal(GenerationReasons.LIBRARY_DOES_NOT_COMPILE, withoutKey.Reason);
        Assert.Contains(withoutKey.Errors, error => error is "CS0122" or "CS0281");
    }

    [Fact]
    public void The_driver_of_a_signed_library_member_carries_the_key_and_compiles_without_CS1726_or_CS0281()
    {
        var driver = Drive(SCHEDULE, Compile(SOURCE, signed: true));

        Assert.Equal(EmittedAssemblies.TestKey, driver.Compilation.Options.CryptoPublicKey.ToArray());
        Assert.True(driver.Compilation.Options.PublicSign);
        Assert.Equal(EmittedAssemblies.TestKey, driver.Compilation.Assembly.Identity.PublicKey.ToArray());
        Assert.DoesNotContain(driver.Compilation.GetDiagnostics(), diagnostic => diagnostic.Id is "CS1726" or "CS0281");
    }

    [Fact]
    public void The_driver_of_an_unsigned_library_member_is_not_signed()
    {
        var driver = Drive(SCHEDULE, Compile(SOURCE, signed: false));

        Assert.True(driver.Compilation.Options.CryptoPublicKey.IsDefaultOrEmpty);
        Assert.False(driver.Compilation.Options.PublicSign);
    }

    [Fact]
    public void An_override_of_a_protected_internal_member_of_a_signed_library_stays_protected_internal()
    {
        var driver = Drive(SCHEDULE, Compile(SOURCE, signed: true));

        Assert.Equal(Accessibility.ProtectedOrInternal, Queue(driver).DeclaredAccessibility);
    }

    [Fact]
    public void An_override_of_a_protected_internal_member_is_protected_when_the_library_names_no_such_friend()
    {
        var byName = Drive(SCHEDULE, Compile(SOURCE, signed: true, openFields: false));
        var other = Drive(SCHEDULE, Compile($"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{DriverSynthesizer.ASSEMBLY}.Other, PublicKey={KeyHex}\")]\n" + SOURCE,
                                            signed: true, openFields: false));
        var bare = Drive(SCHEDULE, Compile(SOURCE, signed: false, openFields: false));

        Assert.Equal(Accessibility.Protected, Queue(byName).DeclaredAccessibility);
        Assert.Equal(Accessibility.Protected, Queue(other).DeclaredAccessibility);
        Assert.Equal(Accessibility.Protected, Queue(bare).DeclaredAccessibility);
    }

    /// <summary>The source of the framework assembly that grants its internal type to the library by name and public key.</summary>
    private static string FriendlySource() => $$"""
        [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("{{LIBRARY}}, PublicKey={{KeyHex}}")]
        namespace Fixture.Friendly
        {
            public static class Anchor { }
            internal sealed class Secret { }
        }
        """;

    /// <summary>The name the library's copy gives the driver's assembly in <c>InternalsVisibleTo</c>.</summary>
    /// <param name="signed">Whether the library is strong-named, so that the name carries its public key.</param>
    private static string DriverFriend(bool signed) =>
        signed ? $"{DriverSynthesizer.ASSEMBLY}, PublicKey={KeyHex}" : DriverSynthesizer.ASSEMBLY;

    /// <summary>The names an assembly grants its internals to, as written.</summary>
    /// <param name="compilation">The compilation.</param>
    private static string[] Friends(Compilation compilation) =>
        compilation.Assembly.GetAttributes()
                   .Where(attribute => attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
                   .Select(attribute => (string)attribute.ConstructorArguments[0].Value!).ToArray();

    /// <summary>The syntax trees of a fixture library, the one a decompiler's output would be.</summary>
    /// <param name="source">The source.</param>
    private static SyntaxTree[] Trees(string source) =>
        [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), $"{LIBRARY}/Library.cs")];

    /// <summary>A fixture library compiled from source, with a key or without.</summary>
    /// <param name="source">The source.</param>
    /// <param name="signed">Whether it is compiled with <see cref="EmittedAssemblies.TestKey"/>.</param>
    /// <param name="openFields">Whether the copy is opened.</param>
    /// <param name="references">The references, or <c>null</c> for the running runtime's.</param>
    private static LibraryCompilationResult Compile(string source, bool signed, bool openFields = true, IReadOnlyList<MetadataReference>? references = null) =>
        LibraryCompilation.CompileTrees(LIBRARY, Trees(source), references ?? EmittedAssemblies.RuntimeReferences, CancellationToken.None,
                                        openFields: openFields, publicKey: signed ? Key : default);

    /// <summary>The compilation of a fixture library that must compile.</summary>
    /// <param name="source">The source.</param>
    /// <param name="signed">Whether it is compiled with <see cref="EmittedAssemblies.TestKey"/>.</param>
    private static CSharpCompilation Compiled(string source, bool signed)
    {
        var result = Compile(source, signed);
        Assert.True(result.Reason is null, string.Join(", ", result.Errors));
        return result.Compilation!;
    }

    /// <summary>An assembly emitted into the install, as the resolver would describe it, against the running runtime.</summary>
    /// <param name="name">The assembly name.</param>
    /// <param name="source">Its source.</param>
    /// <param name="signed">Whether it is compiled with <see cref="EmittedAssemblies.TestKey"/>; a signed one lies in a folder of its own.</param>
    /// <param name="references">Images of the assemblies it uses, or <c>null</c>.</param>
    /// <param name="dependencies">Their files, which it is compiled against too, or <c>null</c>.</param>
    private ImplementationAssembly Lay(string name, string source, bool signed, IReadOnlyList<byte[]>? references = null,
                                       IReadOnlyList<string>? dependencies = null)
    {
        var path = EmittedAssemblies.Write(Path.Combine(_install.Root, signed ? "signed" : "plain", name + ".dll"), name, source,
                                           publicKey: signed ? EmittedAssemblies.TestKey : null, references: references);
        var runtime = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        return new ImplementationAssembly(name, path, null, "1.0.0", $"net{Environment.Version.Major}.0", runtime,
                                          EmittedAssemblies.RUNTIME_ASSEMBLIES.Select(reference => Path.Combine(runtime, reference + ".dll"))
                                                           .Concat(dependencies ?? []).ToArray(),
                                          [], "0.0.0.0", "", Guid.Empty);
    }

    /// <summary>The decompiled assembly compiled by the route a generation takes, without the cache.</summary>
    /// <param name="assembly">The assembly.</param>
    private static LibraryCompilationResult Recompiled(ImplementationAssembly assembly) =>
        LibraryCompilation.CompileModule(assembly.AssemblyName, LibraryCompilation.Decompile(assembly, CancellationToken.None),
                                         assembly.References.Select(reference => (MetadataReference)MetadataReference.CreateFromFile(reference)).ToArray(),
                                         CancellationToken.None);

    /// <summary>The driver of a member, which must compile.</summary>
    /// <param name="id">The member's documentation id.</param>
    /// <param name="library">The compiled library.</param>
    private static Driver Drive(string id, LibraryCompilationResult library)
    {
        Assert.True(library.Reason is null && library.Compilation is not null, string.Join(", ", library.Errors));
        var trace = ModelGenerator.Trace(new GenerationRequest(LIBRARY, "1.0", id, null, null), library, CancellationToken.None);
        Assert.True(trace.Driver is not null, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        Assert.Empty(trace.Driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return trace.Driver;
    }

    /// <summary>The driver's override of <c>Scheduler.Queue</c>.</summary>
    /// <param name="driver">The driver of <c>Api.Schedule</c>.</param>
    private static IMethodSymbol Queue(Driver driver) =>
        driver.Compilation.Assembly.GlobalNamespace.GetTypeMembers()
              .Single(type => type.BaseType?.ToDisplayString() == "Lib.Scheduler").GetMembers("Queue").OfType<IMethodSymbol>().Single();
}
