using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The single answers of <see cref="GenerationTargets"/> that the matrix does not walk: which asset a scope takes, a type
/// forwarded beside one that is not, the two forms of a declaration id, and a token that cancels the resolution.</summary>
public sealed class GenerationTargetTests : IDisposable
{
    private const string PACKAGE = "Acme.Lib";
    private const string API = "namespace Acme { public static class Api { public static void Run(System.Action callback) { } } }";

    private readonly GenerationInstall _install = new();

    public void Dispose() => _install.Dispose();

    [Fact]
    public void Two_versions_of_one_package_are_two_targets()
    {
        _install.SharedFramework("8.0.5");

        var targets = new[] { "1.0.0", "2.0.0" }.Select(version =>
        {
            var path = ReferenceScopes.AddAsset(_install, PACKAGE, version, "net8.0", PACKAGE, Image($"// {version}"));
            var (compilation, method) = Scope(8, version, "net8.0", Image($"// {version}"), "Acme.Api");
            var target = Assert.IsType<GenerationTarget>(GenerationTargets.Of(method, compilation, _install.Resolver(), CancellationToken.None));
            Assert.Equal(AssemblyFile.Read(path)!.Mvid, target.Key.Mvid);
            return target;
        }).ToArray();

        Assert.Equal(targets[0].MemberId, targets[1].MemberId);
        Assert.NotEqual(targets[0].Key, targets[1].Key);
        Assert.Equal(["1.0.0", "2.0.0"], targets.Select(target => target.Key.PackageVersion));
        Assert.Equal(["1.0.0", "2.0.0"], targets.Select(target => target.Request.Version));
        Assert.NotEqual(targets[0].Key.Mvid, targets[1].Key.Mvid);
        Assert.NotEqual(targets[0].Key.RelativePath, targets[1].Key.RelativePath);
    }

    [Fact]
    public void Package_asset_follows_the_scope_framework_not_the_host()
    {
        _install.SharedFramework("6.0.5");
        _install.SharedFramework("8.0.5");
        var assets = new[] { 6, 8 }.ToDictionary(major => major, major => ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", $"net{major}.0", PACKAGE, Image($"// net{major}.0")));
        Assert.NotEqual(AssemblyFile.Read(assets[6])!.Mvid, AssemblyFile.Read(assets[8])!.Mvid);

        foreach (var major in new[] { 6, 8 })
        {
            // The host is .NET 8 for both: the scope's framework, not the resolver's default, picks the asset.
            var (compilation, method) = Scope(major, "1.0.0", $"net{major}.0", Image($"// net{major}.0"), "Acme.Api");
            var target = Assert.IsType<GenerationTarget>(GenerationTargets.Of(method, compilation, _install.Resolver(runtimeMajor: 8), CancellationToken.None));

            Assert.Equal(AssemblyFile.Read(assets[major])!.Mvid, target.Key.Mvid);
            Assert.Equal($"net{major}.0", target.Request.Framework);
        }
    }

    [Fact]
    public void A_dependency_of_a_library_compilation_gets_the_file_key_the_program_computes()
    {
        var shared = _install.SharedFramework("6.0.5");
        _install.SharedFramework("8.0.5");
        var packageImage = Image("// net6.0");
        var asset = ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net6.0", PACKAGE, packageImage);
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, Image("// net8.0"));
        const string FRAMEWORK_ASSEMBLY = "System.Fixture";
        const string FRAMEWORK_API = "namespace Framework { public static class Api { public static void Run(System.Action callback) { } } }";
        var frameworkImage = EmittedAssemblies.Image(FRAMEWORK_ASSEMBLY, ReferenceScopes.VERSION_ATTRIBUTE + FRAMEWORK_API);
        var frameworkPath = Path.Combine(shared, FRAMEWORK_ASSEMBLY + ".dll");
        File.WriteAllBytes(frameworkPath, frameworkImage);
        var program = ReferenceScopes.Compile("public static class App { }", [.. ReferenceScopes.Runtime(_install, 6),
            ReferenceScopes.Reference(packageImage, asset),
            ReferenceScopes.Reference(frameworkImage, ReferenceScopes.PackFile(_install, 6, "Microsoft.NETCore.App.Ref", FRAMEWORK_ASSEMBLY + ".dll"))]);
        // A generator compilation has implementation references, without a reference pack. Its request, rather than the host, selects net6.0.
        var library = ReferenceScopes.Compile("public static class Library { }", [.. ReferenceScopes.Runtime(_install, null),
            ReferenceScopes.Reference(packageImage, asset), ReferenceScopes.Reference(frameworkImage, frameworkPath)]);

        foreach (var type in new[] { "Acme.Api", "Framework.Api" })
        {
            var programTarget = Assert.IsType<GenerationTarget>(GenerationTargets.Of(Run(program, type), program, _install.Resolver(), CancellationToken.None));
            var dependency = Assert.IsType<GenerationTarget>(GenerationTargets.Of(Run(library, type), library, _install.Resolver(), CancellationToken.None, 6));

            Assert.Equal(programTarget.Key, dependency.Key);
            Assert.Equal(programTarget.Request, dependency.Request);
            if (type == "Acme.Api")
            {
                Assert.Equal(AssemblyFile.Read(asset)!.Mvid, dependency.Key.Mvid);
                Assert.Equal("net6.0", dependency.Request.Framework);
            }
            else
            {
                Assert.Equal("Microsoft.NETCore.App", dependency.Key.Framework);
                Assert.Equal(6, dependency.Key.Major);
                Assert.Equal("6.0", dependency.Request.Version);
            }
        }
    }

    [Fact]
    public void Types_of_one_assembly_forwarded_apart_get_their_own_answers_in_either_order()
    {
        _install.SharedFramework("8.0.5");
        const string TYPES = """
            namespace Acme
            {
                public static class Own { public static void Run(System.Action callback) { } }
                public static class ToOther { public static void Run(System.Action callback) { } }
                public static class ToCore { public static void Run(System.Action callback) { } }
            }
            """;
        // The reference declares the three types; the implementation keeps one and forwards the others.
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE,
                        ForwardingAssemblies.Image(PACKAGE, ("Acme", "ToOther", "System.Collections"), ("Acme", "ToCore", "System.Private.CoreLib")));
        var reference = EmittedAssemblies.Image(PACKAGE, ReferenceScopes.VERSION_ATTRIBUTE + TYPES);
        var compilation = ReferenceScopes.Compile("public static class App { }", [.. ReferenceScopes.Runtime(_install, 8),
                                                                       ReferenceScopes.Reference(reference, ReferenceScopes.PackagePath(_install, PACKAGE, "1.0.0", "lib", "net8.0", PACKAGE))]);
        var own = Run(compilation, "Acme.Own");
        var toOther = Run(compilation, "Acme.ToOther");
        var toCore = Run(compilation, "Acme.ToCore");
        string[] Ask(params IMethodSymbol[] methods) =>
            methods.Select(method => GenerationTargets.Of(method, compilation, _install.Resolver(), CancellationToken.None))
                   .Select(answer => answer is NoGenerationTarget none ? $"NoTarget {none.Reason}" : answer.GetType().Name)
                   .ToArray();

        Assert.Equal(["GenerationTarget", "NoTarget Forwarded", "NoTarget Forwarded"], Ask(own, toOther, toCore));
        Assert.Equal(["NoTarget Forwarded", "NoTarget Forwarded", "GenerationTarget"], Ask(toCore, toOther, own));
        Assert.Equal(["NoTarget Forwarded", "GenerationTarget", "NoTarget Forwarded"], Ask(toOther, own, toCore));
    }

    [Fact]
    public void Missing_assets_of_one_package_version_in_two_compilations_are_one_key()
    {
        _install.SharedFramework("6.0.5");
        _install.SharedFramework("8.0.5");
        // Only a .NET Framework asset: neither a net6.0 nor a net8.0 scope takes it.
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net462", PACKAGE, Image(""));

        var answers = new[] { 6, 8 }.Select(major =>
        {
            var (compilation, method) = Scope(major, "1.0.0", $"net{major}.0", Image(""), "Acme.Api");
            return Assert.IsType<UnavailableGenerationTarget>(GenerationTargets.Of(method, compilation, _install.Resolver(), CancellationToken.None));
        }).ToArray();

        Assert.Equal(answers[0].Key, answers[1].Key);
        Assert.Null(answers[0].Key.Mvid);
        Assert.Null(answers[0].Key.RelativePath);
        Assert.Equal((PACKAGE, "1.0.0"), (answers[0].Key.Package, answers[0].Key.PackageVersion));
        Assert.Contains("net6.0", answers[0].Detail);
        Assert.Contains("net8.0", answers[1].Detail);
    }

    [Fact]
    public void Member_and_lookup_ids_of_a_non_void_method_and_a_getter()
    {
        _install.SharedFramework("8.0.5");
        const string LIBRARY = """
            namespace Acme
            {
                public static class Api
                {
                    public static int Compute(int value) => value;
                    public static void Run(System.Action callback) { }
                    public static string Name => "";
                }

                public static class Ext { public static int Twice(this int value) => value * 2; }
            }
            """;
        var image = EmittedAssemblies.Image(PACKAGE, ReferenceScopes.VERSION_ATTRIBUTE + LIBRARY);
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, image);
        var compilation = ReferenceScopes.Compile("using Acme; class App { int F(int value) => value.Twice(); }",
                                         [.. ReferenceScopes.Runtime(_install, 8), ReferenceScopes.Reference(image, ReferenceScopes.PackagePath(_install, PACKAGE, "1.0.0", "lib", "net8.0", PACKAGE))]);
        var api = compilation.GetTypeByMetadataName("Acme.Api")!;
        var tree = compilation.SyntaxTrees.Single();
        // `value.Twice()` is the extension method reduced to an instance call, whose ids are those of the method it reduces.
        var reduced = (IMethodSymbol)compilation.GetSemanticModel(tree).GetSymbolInfo(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single()).Symbol!;
        Assert.NotNull(reduced.ReducedFrom);

        (string Member, string Lookup) Ids(IMethodSymbol method)
        {
            var target = Assert.IsType<GenerationTarget>(GenerationTargets.Of(method, compilation, _install.Resolver(), CancellationToken.None));
            return (target.MemberId, target.LookupId);
        }

        // A non-void method's lookup form adds `~` and the return type to the form the generator writes; a void method's agree.
        Assert.Equal(("M:Acme.Api.Compute(System.Int32)", "M:Acme.Api.Compute(System.Int32)~System.Int32"),
                     Ids(api.GetMembers("Compute").OfType<IMethodSymbol>().Single()));
        Assert.Equal(("M:Acme.Api.get_Name", "M:Acme.Api.get_Name~System.String"), Ids(api.GetMembers("get_Name").OfType<IMethodSymbol>().Single()));
        Assert.Equal(("M:Acme.Api.Run(System.Action)", "M:Acme.Api.Run(System.Action)"), Ids(api.GetMembers("Run").OfType<IMethodSymbol>().Single()));
        Assert.Equal(("M:Acme.Ext.Twice(System.Int32)", "M:Acme.Ext.Twice(System.Int32)~System.Int32"), Ids(reduced));
    }

    [Fact]
    public void Cancelled_token_stops_target_resolution()
    {
        _install.SharedFramework("8.0.5");
        ReferenceScopes.AddAsset(_install, PACKAGE, "1.0.0", "net8.0", PACKAGE, Image(""));
        var (compilation, method) = Scope(8, "1.0.0", "net8.0", Image(""), "Acme.Api");
        var reads = new List<string>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => GenerationTargets.Of(method, compilation, _install.Resolver(nuspecRead: reads.Add), cancelled.Token));
        Assert.Empty(reads);

        // The same call with a live token resolves, and so reads the nuspec.
        Assert.IsType<GenerationTarget>(GenerationTargets.Of(method, compilation, _install.Resolver(nuspecRead: reads.Add), CancellationToken.None));
        Assert.NotEmpty(reads);

        // A token that fires while the resolver reads the nuspec stops the resolution too: the resolver observes the token it is given.
        using var midway = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => GenerationTargets.Of(method, compilation, _install.Resolver(nuspecRead: _ => midway.Cancel()), midway.Token));
    }

    [Fact]
    public void Member_of_corelib_is_no_target()
    {
        // The reference pack of a real compilation has no corelib; one that has is answered before anything is read of the implementation.
        var corelib = ReferenceScopes.Reference(File.ReadAllBytes(typeof(object).Assembly.Location),
                                       ReferenceScopes.PackFile(_install, 8, "Microsoft.NETCore.App.Ref", "System.Private.CoreLib.dll"));
        var compilation = ReferenceScopes.Compile("public static class App { }", [corelib, EmittedAssemblies.RuntimeReferences[1]]);
        var toString = compilation.GetSpecialType(SpecialType.System_Object).GetMembers("ToString").OfType<IMethodSymbol>().Single();

        var answer = GenerationTargets.Of(toString, compilation, _install.Resolver(), CancellationToken.None);

        Assert.Equal(new NoGenerationTarget(NoTargetReason.CoreLib), answer);
    }

    [Fact]
    public void Id_that_resolves_to_another_member_names_no_target()
    {
        // Roslyn writes nothing for a function-pointer type, so F(delegate*<void>) has the id of "F()", which finds the parameterless F.
        const string SOURCE = "namespace P { public static unsafe class A { public static void F() { } public static void F(delegate*<void> callback) { } " +
                              "public static void G(System.Action callback) { } } }";
        var compilation = ReferenceScopes.Compile(SOURCE, ReferenceScopes.Runtime(_install, 8));
        var methods = compilation.GetTypeByMetadataName("P.A")!.GetMembers().OfType<IMethodSymbol>().Where(method => method.Name is "F" or "G").ToArray();
        var pointer = methods.Single(method => FunctionPointers.In(method));
        var sibling = methods.Single(method => method.Name == "F" && method.Parameters.Length == 0);
        var other = methods.Single(method => method.Name == "G");

        Assert.Equal("M:P.A.F()", pointer.GetDocumentationCommentId());
        Assert.False(GenerationTargets.NamesOnly(pointer, pointer.GetDocumentationCommentId()!, compilation));
        Assert.True(GenerationTargets.NamesOnly(sibling, sibling.GetDocumentationCommentId()!, compilation));
        Assert.True(GenerationTargets.NamesOnly(other, other.GetDocumentationCommentId()!, compilation));
    }

    /// <summary>The package's library, told apart from another by a comment.</summary>
    /// <param name="comment">What tells it apart: a different source is a different MVID.</param>
    private static byte[] Image(string comment) => EmittedAssemblies.Image(PACKAGE, ReferenceScopes.VERSION_ATTRIBUTE + API + "\n" + comment);

    /// <summary>A scope that references the package's asset under a framework's folder, as a project of that framework does.</summary>
    /// <param name="packMajor">The major of the scope's <c>Microsoft.NETCore.App.Ref</c>.</param>
    /// <param name="version">The package version.</param>
    /// <param name="framework">The framework folder of the asset the scope references.</param>
    /// <param name="image">The asset the scope references.</param>
    /// <param name="type">The type whose <c>Run</c> is returned.</param>
    private (Compilation Compilation, IMethodSymbol Method) Scope(int packMajor, string version, string framework, byte[] image, string type)
    {
        var compilation = ReferenceScopes.Compile("public static class App { }", [.. ReferenceScopes.Runtime(_install, packMajor),
                                                                       ReferenceScopes.Reference(image, ReferenceScopes.PackagePath(_install, PACKAGE, version, "lib", framework, PACKAGE))]);
        return (compilation, Run(compilation, type));
    }

    private static IMethodSymbol Run(Compilation compilation, string type) =>
        compilation.GetTypeByMetadataName(type)!.GetMembers("Run").OfType<IMethodSymbol>().Single();
}
