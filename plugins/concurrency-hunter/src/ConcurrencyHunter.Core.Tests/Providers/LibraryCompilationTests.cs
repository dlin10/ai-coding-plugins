using System.Runtime.InteropServices;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The model generator's library compilation (SPEC TD-034b): a decompiled implementation compiled as its own library, a
/// body that does not compile rewritten as <c>extern</c> so the engine reads a call into it as a call without a body.</summary>
public sealed class LibraryCompilationTests
{
    private const string ASYNC_SOURCE = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        public static class Work
        {
            public static async Task<int> TwiceAsync(Task<int> value)
            {
                var first = await value;
                await Task.Yield();
                return first * 2;
            }

            public static IEnumerable<int> Count(int n)
            {
                for (var i = 0; i < n; i++)
                    yield return i;
            }

            public static async IAsyncEnumerable<int> StreamAsync(int n)
            {
                for (var i = 0; i < n; i++)
                {
                    await Task.Yield();
                    yield return i;
                }
            }

            public static Func<int, int> Adder(int k) => x => x + k;
        }
        """;

    private static readonly Lazy<LinqLibrary> Linq = new(() =>
    {
        var resolution = ImplementationAssemblies.ForThisProcess().Resolve("System.Linq", Environment.Version.Major.ToString(), null, null);
        Assert.True(resolution.Reason is null, resolution.Detail);
        return new LinqLibrary(resolution.Assembly!, LibraryCompilation.Decompile(resolution.Assembly!, CancellationToken.None));
    });

    [Fact]
    public void Installed_System_Linq_decompiles_to_one_tree_per_type()
    {
        var trees = Linq.Value.Module.Trees;

        Assert.Empty(Linq.Value.Module.Failures);
        Assert.Equal("AssemblyInfo.cs", Path.GetFileName(trees[^1].FilePath));
        Assert.Equal(TopLevelTypes(Linq.Value.Assembly.Path).Select(type => $"System.Linq/{FileName(type)}.cs").Order(StringComparer.Ordinal),
                     trees.SkipLast(1).Select(tree => tree.FilePath).Order(StringComparer.Ordinal));
        foreach (var tree in trees.SkipLast(1))
        {
            var types = tree.GetRoot().DescendantNodes()
                            .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax &&
                                           node.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
                            .ToArray();
            Assert.True(types.Length == 1, $"{tree.FilePath} declares {types.Length} top-level types");
        }

        Assert.Contains(trees, tree => tree.FilePath == "System.Linq/System.Linq.Enumerable.cs");
        Assert.Equal(trees.Count, trees.Select(tree => tree.FilePath).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Installed_System_Linq_compiles_with_no_error_in_any_body_after_the_rewrite()
    {
        var result = LibraryCompilation.Compile(Linq.Value.Assembly, CancellationToken.None);

        Assert.True(result.Reason is null, $"{result.Reason}: {string.Join(", ", result.Errors)}");
        Assert.NotNull(result.Compilation);
        var errors = result.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.All(errors, error => Assert.Contains(error.Id, new[] { "CS0102", "CS0216", "CS8335" }));
        Assert.True(result.Bodies > 500, $"{result.Bodies} bodies");
        Assert.True(result.ExternBodies * 100 < result.Bodies, $"{result.ExternBodies} of {result.Bodies} bodies made extern");
        Assert.Equal("System.Linq", result.Compilation.AssemblyName);
        Assert.NotNull(result.Compilation.GetTypeByMetadataName("System.Linq.Enumerable"));
    }

    [Fact]
    public void An_async_method_of_an_emitted_assembly_decompiles_to_valid_identifiers()
    {
        using var install = new GenerationInstall();
        var assembly = Emitted(install, "Fixture.Async", ASYNC_SOURCE);

        var trees = LibraryCompilation.Decompile(assembly, CancellationToken.None).Trees;
        var result = LibraryCompilation.Compile(assembly, CancellationToken.None);

        var identifiers = trees.SelectMany(tree => tree.GetRoot().DescendantTokens()).Where(token => token.IsKind(SyntaxKind.IdentifierToken)).ToArray();
        Assert.NotEmpty(identifiers);
        Assert.All(identifiers, identifier => Assert.True(SyntaxFacts.IsValidIdentifier(identifier.ValueText), identifier.ValueText));
        Assert.Null(result.Reason);
        Assert.Equal(0, result.ExternBodies);
        var work = result.Compilation!.GetTypeByMetadataName("Work")!;
        Assert.True(work.GetMembers("TwiceAsync").OfType<IMethodSymbol>().Single().IsAsync);
        Assert.True(work.GetMembers("StreamAsync").OfType<IMethodSymbol>().Single().IsAsync);
    }

    [Fact]
    public void A_method_with_a_body_error_becomes_extern_and_a_direct_call_to_it_is_an_opaque_call()
    {
        var result = CompileSource("""
            public sealed class Ledger { public int Total; }

            public static class Library
            {
                public static void Post(Ledger ledger) { ledger.Total = "not a number"; }
                public static void Fine(Ledger ledger) { ledger.Total = 1; }
            }
            """);

        Assert.Null(result.Reason);
        Assert.True(Method(result, "Library", "Post").IsExtern);
        Assert.False(Method(result, "Library", "Fine").IsExtern);
        Assert.Equal(["M:Library.Post(Ledger)"], result.ExternMembers);
        Assert.Equal(1, result.ExternBodies);
        Assert.Equal(2, result.Bodies);

        var run = Analyze(result, "Library.Post(Shared);");
        var gap = Assert.Single(run.Collection.Coverage.Gaps, gap => gap.Callee == "Library.Post(Ledger)");
        Assert.Equal(SemanticGapKinds.UNKNOWN_LIBRARY, gap.Kind);
        Assert.DoesNotContain(run.Collection.Coverage.Gaps, gap => gap.Callee.StartsWith("Library.Fine", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_member_of_Unsafe_becomes_extern_and_a_type_of_its_name_elsewhere_keeps_its_bodies()
    {
        // CoreLib's bodies of Unsafe are placeholders the runtime replaces, or IL the decompiler renders as a call to the member itself.
        var result = CompileSource("""
            using System;

            namespace System.Runtime.CompilerServices
            {
                public static class Unsafe
                {
                    public static unsafe void* AsPointer<T>(ref T value) { throw new PlatformNotSupportedException(); }
                    public static unsafe T Read<T>(void* source) => Read<T>(source);
                    public static extern T As<T>(object o) where T : class;
                }
            }

            namespace Lib
            {
                public static class Unsafe { public static object Same(object value) => value; }
            }
            """);

        Assert.Null(result.Reason);
        Assert.True(Method(result, "System.Runtime.CompilerServices.Unsafe", "AsPointer").IsExtern);
        Assert.True(Method(result, "System.Runtime.CompilerServices.Unsafe", "Read").IsExtern);
        Assert.False(Method(result, "Lib.Unsafe", "Same").IsExtern);
        Assert.Equal(["M:System.Runtime.CompilerServices.Unsafe.AsPointer``1(``0@)", "M:System.Runtime.CompilerServices.Unsafe.Read``1(System.Void*)"],
                     result.ExternMembers);
        Assert.Equal(2, result.ExternBodies);
    }

    [Fact]
    public void A_body_whose_only_statement_calls_the_member_itself_becomes_extern()
    {
        // MemoryMarshal.GetArrayDataReference and the hardware intrinsics: the JIT replaces the body, and its IL is a call to the member
        // itself, which recursion with no way out reads as returning nothing.
        var result = CompileSource("""
            public static class Memory
            {
                public static ref T DataOf<T>(T[] array) => ref DataOf(array);
                public static int Add(int left, int right) { return Add(left, right); }
                public static void Barrier() { Barrier(); }
                public static int Count(int n) { if (n == 0) return 0; return Count(n - 1) + 1; }
                public static int Other(int n) => Count(n);
            }
            """);

        Assert.Null(result.Reason);
        Assert.True(Method(result, "Memory", "DataOf").IsExtern);
        Assert.True(Method(result, "Memory", "Add").IsExtern);
        Assert.True(Method(result, "Memory", "Barrier").IsExtern);
        Assert.False(Method(result, "Memory", "Count").IsExtern);
        Assert.False(Method(result, "Memory", "Other").IsExtern);
        Assert.Equal(["M:Memory.Add(System.Int32,System.Int32)", "M:Memory.Barrier", "M:Memory.DataOf``1(``0[])"], result.ExternMembers);
    }

    [Fact]
    public void An_async_method_in_error_loses_async()
    {
        var result = CompileSource("""
            using System.Threading.Tasks;

            public static class Library
            {
                public static async Task<int> RunAsync() { await Task.Yield(); int value = "text"; return value; }
                public static async Task FineAsync() { await Task.Yield(); }
            }
            """);

        Assert.Null(result.Reason);
        var run = Method(result, "Library", "RunAsync");
        Assert.True(run.IsExtern);
        Assert.False(run.IsAsync);
        Assert.True(Method(result, "Library", "FineAsync").IsAsync);
        Assert.DoesNotContain(result.Compilation!.GetDiagnostics(), diagnostic => diagnostic.Id == "CS1994");
    }

    [Fact]
    public void An_error_inside_a_lambda_or_a_local_function_makes_its_enclosing_method_extern()
    {
        var result = CompileSource("""
            using System;

            public static class Library
            {
                public static Func<int> Make() { Func<int> make = () => { int value = "text"; return value; }; return make; }
                public static int Local() { return Inner(); int Inner() => Missing.Value; }
                public static Func<int> Fine() => () => 1;
            }
            """);

        Assert.Null(result.Reason);
        Assert.True(Method(result, "Library", "Make").IsExtern);
        Assert.True(Method(result, "Library", "Local").IsExtern);
        Assert.False(Method(result, "Library", "Fine").IsExtern);
        Assert.Equal(["M:Library.Local", "M:Library.Make"], result.ExternMembers);
    }

    [Fact]
    public void An_ambiguous_type_name_after_a_using_compiles_whole()
    {
        // System.Text.Json's converters: the decompiler sees the library's own BooleanConverter and writes its simple name after
        // `using System.ComponentModel`, whose BooleanConverter lives in an assembly the library does not reference; only the compilation
        // against the whole framework sees both.
        using var install = new GenerationInstall();
        var emitted = Emitted(install, "Fixture.Ambiguous", """
            using System.ComponentModel;
            using Lib.Converters;

            namespace Lib.Converters
            {
                public sealed class BooleanConverter { public int Value; }
            }

            namespace Lib.Metadata
            {
                [EditorBrowsable(EditorBrowsableState.Never)]
                public static class Services
                {
                    public static BooleanConverter Shared { get; } = new BooleanConverter();
                    public static int Read() => new BooleanConverter().Value;
                }
            }
            """);
        var assembly = emitted with
        {
            References = [.. emitted.References, Path.Combine(emitted.SharedFrameworkDirectory, "System.ComponentModel.TypeConverter.dll")]
        };
        var module = LibraryCompilation.Decompile(assembly, CancellationToken.None);
        var references = assembly.References.Select(reference => (MetadataReference)MetadataReference.CreateFromFile(reference)).ToArray();

        var kept = LibraryCompilation.CompileModule(assembly.AssemblyName, module, references, CancellationToken.None, repairArtefacts: false);
        var repaired = LibraryCompilation.CompileModule(assembly.AssemblyName, module, references, CancellationToken.None);

        Assert.Equal(GenerationReasons.LIBRARY_DOES_NOT_COMPILE, kept.Reason);
        Assert.Contains("CS0104", kept.Errors);
        Assert.True(repaired.Reason is null, $"{repaired.Reason}: {string.Join(", ", repaired.Errors)}");
        Assert.Equal(0, repaired.ExternBodies);
        Assert.Empty(repaired.ExternMembers);
        Assert.DoesNotContain(repaired.Compilation!.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error &&
                                                                                   !LibraryCompilation.BenignDeclarationErrors.Contains(diagnostic.Id));
        var services = repaired.Compilation.GetTypeByMetadataName("Lib.Metadata.Services")!;
        Assert.Equal("Lib.Converters.BooleanConverter", services.GetMembers("Shared").OfType<IPropertySymbol>().Single().Type.ToDisplayString());

        // A name ambiguous between two types the decompiler saw is no artefact of it, and the repair does not choose: the body stays extern.
        var both = CompileSource("""
            using A;
            using B;

            namespace A { public sealed class Item { } }
            namespace B { public sealed class Item { } }
            namespace C
            {
                public static class Use
                {
                    public static object Make() => new Item();
                    public static object Fine() => new A.Item();
                }
            }
            """);
        Assert.Null(both.Reason);
        Assert.Equal(["M:C.Use.Make"], both.ExternMembers);
    }

    [Fact]
    public void A_local_function_with_a_display_class_in_its_signature_compiles_whole()
    {
        // AutoMapper 14's ProjectionBuilder as the decompiler leaves it: static local functions its compiler emitted as internal methods,
        // called from a local function the decompiler left in a class display class, take a private struct display class by reference
        // (CS0051) or return one (CS0050).
        var decompiled = new DecompiledModule([CSharpSyntaxTree.ParseText("""
            using System.Runtime.CompilerServices;

            namespace Lib
            {
                public sealed class Builder
                {
                    [CompilerGenerated]
                    private struct _003C_003Ec__DisplayClass0_1
                    {
                        public int first;
                    }

                    [CompilerGenerated]
                    private sealed class _003C_003Ec__DisplayClass0_0
                    {
                        public int limit;

                        internal bool _003CCreate_003Eg__Outer_007C1(int item)
                        {
                            _003C_003Ec__DisplayClass0_1 obj = _003CCreate_003Eg__Make_007C0_3(item);
                            return _003CCreate_003Eg__Both_007C0_2(ref obj, limit);
                        }
                    }

                    public bool Create(int item, int limit) => new _003C_003Ec__DisplayClass0_0 { limit = limit }._003CCreate_003Eg__Outer_007C1(item);

                    [CompilerGenerated]
                    internal static bool _003CCreate_003Eg__Both_007C0_2(ref _003C_003Ec__DisplayClass0_1 P_0, int limit)
                    {
                        return P_0.first > limit;
                    }

                    [CompilerGenerated]
                    internal static _003C_003Ec__DisplayClass0_1 _003CCreate_003Eg__Make_007C0_3(int item)
                    {
                        _003C_003Ec__DisplayClass0_1 result = default;
                        result.first = item;
                        return result;
                    }
                }
            }
            """, new CSharpParseOptions(LanguageVersion.Preview), "Fixture.Library/Lib.Builder.cs")], []);

        var kept = LibraryCompilation.CompileModule("Fixture.Library", decompiled, EmittedAssemblies.RuntimeReferences, CancellationToken.None,
                                                    repairArtefacts: false);
        var repaired = LibraryCompilation.CompileModule("Fixture.Library", decompiled, EmittedAssemblies.RuntimeReferences, CancellationToken.None);

        Assert.Equal(GenerationReasons.LIBRARY_DOES_NOT_COMPILE, kept.Reason);
        Assert.Contains("CS0051", kept.Errors);
        Assert.Contains("CS0050", kept.Errors);
        Assert.True(repaired.Reason is null, $"{repaired.Reason}: {string.Join(", ", repaired.Errors)}");
        Assert.Equal(0, repaired.ExternBodies);
        Assert.Empty(repaired.ExternMembers);
        Assert.Empty(repaired.Compilation!.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Equal(Accessibility.Private, Method(repaired, "Lib.Builder", "_003CCreate_003Eg__Both_007C0_2").DeclaredAccessibility);
        Assert.Equal(Accessibility.Private, Method(repaired, "Lib.Builder", "_003CCreate_003Eg__Make_007C0_3").DeclaredAccessibility);

        // A method the compiler did not generate, or one whose signature names no compiler-generated type, is no unfolded local function.
        foreach (var (method, hidden) in new[] { ("", "[CompilerGenerated]"), ("[CompilerGenerated]", "") })
        {
            var refused = CompileSource($$"""
                using System.Runtime.CompilerServices;

                public sealed class Holder
                {
                    {{hidden}}
                    private struct Hidden { }

                    {{method}}
                    internal static void Use(ref Hidden hidden) { }
                }
                """);

            Assert.Equal(GenerationReasons.LIBRARY_DOES_NOT_COMPILE, refused.Reason);
            Assert.Contains("CS0051", refused.Errors);
        }
    }

    [Fact]
    public void A_property_or_indexer_accessor_in_error_makes_the_member_extern()
    {
        var result = CompileSource("""
            public sealed class Holder
            {
                private int _value;
                public int Value { get { return Missing.Value; } set { _value = value; } }
                public int Twice => Missing.Value * 2;
                public int this[int index] { get => _value; set => _value = "text"; }
                public int Fine { get => _value; set => _value = value; }
            }
            """);

        Assert.Null(result.Reason);
        var holder = result.Compilation!.GetTypeByMetadataName("Holder")!;
        foreach (var name in new[] { "Value", "Twice", "this[]" })
        {
            var property = holder.GetMembers(name).OfType<IPropertySymbol>().Single();
            Assert.True(property.IsExtern, name);
            Assert.All(new[] { property.GetMethod, property.SetMethod }.OfType<IMethodSymbol>(), accessor => Assert.True(accessor.IsExtern, accessor.Name));
        }

        Assert.False(holder.GetMembers("Fine").OfType<IPropertySymbol>().Single().IsExtern);
        Assert.Contains("P:Holder.Value", result.ExternMembers);
        Assert.Contains("M:Holder.get_Value", result.ExternMembers);
        Assert.Contains("M:Holder.set_Value(System.Int32)", result.ExternMembers);
        Assert.Contains("M:Holder.get_Twice", result.ExternMembers);
        Assert.Contains("P:Holder.Item(System.Int32)", result.ExternMembers);
        Assert.Equal(5, result.ExternBodies);
        Assert.Empty(result.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Installed_System_Text_Json_and_System_Net_Security_compile()
    {
        foreach (var name in new[] { "System.Text.Json", "System.Net.Security" })
        {
            var resolution = ImplementationAssemblies.ForThisProcess().Resolve(name, "10.0", null, "net10.0");
            Assert.True(resolution.Reason is null, resolution.Detail);
            var result = LibraryCompilation.Compile(resolution.Assembly!, CancellationToken.None);

            Assert.True(result.Reason is null, $"{name}: {result.Reason}: {string.Join(", ", result.Errors)}");
            Assert.NotNull(result.Compilation);
        }

        // System.Text.Json compiled directly, past the cache, without the repair of the decompiler's artefacts and with it: the converters
        // named after `using System.ComponentModel` (CS0104) keep their bodies, and no body that compiled without the repair loses its own.
        var json = ImplementationAssemblies.ForThisProcess().Resolve("System.Text.Json", "10.0", null, "net10.0").Assembly!;
        var module = LibraryCompilation.Decompile(json, CancellationToken.None);
        var references = json.References.Select(reference => (MetadataReference)MetadataReference.CreateFromFile(reference)).ToArray();
        var kept = LibraryCompilation.CompileModule(json.AssemblyName, module, references, CancellationToken.None, repairArtefacts: false);
        var repaired = LibraryCompilation.CompileModule(json.AssemblyName, module, references, CancellationToken.None);
        var ambiguous = AmbiguousMembers(json.AssemblyName, module, references);

        Assert.True(kept.Reason is null, $"{kept.Reason}: {string.Join(", ", kept.Errors)}");
        Assert.True(repaired.Reason is null, $"{repaired.Reason}: {string.Join(", ", repaired.Errors)}");
        Assert.Subset(kept.ExternMembers.ToHashSet(StringComparer.Ordinal), repaired.ExternMembers.ToHashSet(StringComparer.Ordinal));
        var gone = kept.ExternMembers.Except(repaired.ExternMembers, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(ambiguous, gone);
        Assert.True(gone.Count >= 24, $"{gone.Count} members keep their bodies: {string.Join(", ", gone)}");
        Assert.Empty(ambiguous.Intersect(repaired.ExternMembers, StringComparer.Ordinal));
    }

    /// <summary>The documentation ids of the members whose declaration or body has a CS0104 error in a decompiled module compiled as
    /// it is, a property's with its accessors'.</summary>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="module">The decompiled module.</param>
    /// <param name="references">The references of the platform version.</param>
    private static HashSet<string> AmbiguousMembers(string assemblyName, DecompiledModule module, IReadOnlyList<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create(assemblyName, module.Trees, references,
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                                                                                nullableContextOptions: NullableContextOptions.Disable));
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var error in compilation.GetDiagnostics().Where(diagnostic => diagnostic.Id == "CS0104"))
        {
            var tree = error.Location.SourceTree!;
            var member = tree.GetRoot().FindNode(error.Location.SourceSpan).AncestorsAndSelf().OfType<MemberDeclarationSyntax>().First();
            var symbol = compilation.GetSemanticModel(tree).GetDeclaredSymbol(member);
            ISymbol?[] declared = symbol is IPropertySymbol property ? [property, property.GetMethod, property.SetMethod] : [symbol];
            members.UnionWith(declared.OfType<ISymbol>().Select(declaredSymbol => declaredSymbol.GetDocumentationCommentId()!));
        }
        return members;
    }

    [Theory]
    [InlineData("set", "", false)]
    [InlineData("set", "private", false)]
    [InlineData("set", "internal", false)]
    [InlineData("set", "protected", false)]
    [InlineData("init", "", false)]
    [InlineData("init", "private", false)]
    [InlineData("init", "internal", false)]
    [InlineData("init", "protected", false)]
    [InlineData("set", "", true)]
    public void A_trimmed_auto_property_without_a_getter_gets_one(string setter, string modifier, bool isStatic)
    {
        var result = CompileSource($$"""
            public class Holder
            {
                public {{(isStatic ? "static" : "")}} int Value { {{modifier}} {{setter}}; }
            }
            """);

        Assert.True(result.Reason is null, $"{result.Reason}: {string.Join(", ", result.Errors)}");
        Assert.Empty(result.Compilation!.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var property = result.Compilation.GetTypeByMetadataName("Holder")!.GetMembers("Value").OfType<IPropertySymbol>().Single();
        Assert.NotNull(property.GetMethod);
        Assert.Equal(Accessibility.Public, property.GetMethod.DeclaredAccessibility);
        Assert.False(property.SetMethod!.IsExtern);
        Assert.Equal(modifier switch
        {
            "private" => Accessibility.Private,
            "internal" => Accessibility.Internal,
            "protected" => Accessibility.Protected,
            _ => Accessibility.Public
        }, property.SetMethod.DeclaredAccessibility);
        Assert.Equal(setter == "init", property.SetMethod.IsInitOnly);
        var syntax = result.Compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>().Single();
        Assert.All(syntax.AccessorList!.Accessors, accessor =>
        {
            Assert.Null(accessor.Body);
            Assert.Null(accessor.ExpressionBody);
            Assert.True(accessor.SemicolonToken.IsKind(SyntaxKind.SemicolonToken));
        });
        Assert.Empty(result.ExternMembers);
    }

    [Fact]
    public void An_accessor_modifier_without_a_trimmed_getter_is_not_rewritten()
    {
        var result = CompileSource("public class Holder { public int Value { private get; } }");

        Assert.Equal(GenerationReasons.LIBRARY_DOES_NOT_COMPILE, result.Reason);
        Assert.Null(result.Compilation);
        Assert.Contains("CS0276", result.Errors);
        Assert.Empty(result.ExternMembers);
    }

    [Fact]
    public void An_explicit_interface_event_in_error_calls_a_bodiless_member()
    {
        var result = CompileSource("""
            public interface ISource { event System.Action Changed; }
            public class Source : ISource
            {
                event System.Action ISource.Changed { add { Missing.Log(value); } remove { } }
            }
            """);

        var method = Assert.Single(EventForwarders(result));
        Assert.False(method.IsStatic);
        Assert.Equal("Changed__extern", method.Name);
        Assert.Equal(2, result.ExternBodies);
    }

    [Fact]
    public void A_static_explicit_interface_event_in_error_calls_a_static_bodiless_member()
    {
        var result = CompileSource("""
            public interface ISource<T> where T : ISource<T> { static abstract event System.Action Changed; }
            public class Source : ISource<Source>
            {
                static event System.Action ISource<Source>.Changed { add { Missing.Log(value); } remove { } }
            }
            """);

        Assert.True(Assert.Single(EventForwarders(result)).IsStatic);
    }

    [Theory]
    [InlineData("two-instance", false)]
    [InlineData("instance-and-static", false)]
    [InlineData("member", false)]
    [InlineData("partial", false)]
    [InlineData("type", false)]
    [InlineData("type", true)]
    [InlineData("type-parameter", false)]
    [InlineData("type-parameter", true)]
    public void Explicit_interface_events_in_error_get_distinct_bodiless_members(string collision, bool isStatic)
    {
        var typeName = collision == "type" ? "Changed__extern" : "Source";
        var typeParameters = collision == "type-parameter" ? "<Changed__extern>" : "";
        var type = typeName + typeParameters;
        var declaration = isStatic ? "static " : "";
        var contract = isStatic ? $"IStaticSource<{type}>" : "ISource";
        var otherContract = collision == "instance-and-static" ? $"IStaticSource<{type}>" : "IOtherSource";
        var secondEvent = collision is "two-instance" or "instance-and-static";
        var result = CompileSource($$"""
            public interface ISource { event System.Action Changed; }
            public interface IOtherSource { event System.Action Changed; }
            public interface IStaticSource<T> where T : IStaticSource<T> { static abstract event System.Action Changed; }
            public partial class {{type}} : {{contract}}{{(secondEvent ? ", " + otherContract : "")}}
            {
                {{declaration}}event System.Action {{contract}}.Changed { add { Missing.Log(); } remove { } }
                {{(secondEvent ? (collision == "instance-and-static" ? "static " : "") + "event System.Action " + otherContract + ".Changed { add { } remove { Missing.Log(); } }" : "")}}
                {{(collision == "member" ? "private void Changed__extern() { } private int Changed__extern1;" : "")}}
            }
            {{(collision == "partial" ? "public partial class " + type + " { private int Changed__extern; private void Changed__extern1() { } }" : "")}}
            """);

        var methods = EventForwarders(result);
        Assert.Equal(secondEvent ? 2 : 1, methods.Count);
        Assert.Equal(methods.Count, methods.Select(method => method.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(methods, method =>
        {
            Assert.NotEqual(method.ContainingType.Name, method.Name);
            Assert.DoesNotContain(method.ContainingType.TypeParameters, parameter => parameter.Name == method.Name);
            Assert.Single(method.ContainingType.GetMembers(method.Name));
        });
        Assert.Contains(methods, method => method.Name == (collision is "member" or "partial" ? "Changed__extern2" : "Changed__extern1"));
    }

    [Fact]
    public void Subscribing_through_the_interface_to_an_explicit_event_in_error_hands_the_delegate_to_an_opaque_call()
    {
        var result = CompileSource("""
            public sealed class Ledger { public int Total; }
            public interface ISource { event System.Action Changed; }
            public sealed class Source : ISource
            {
                event System.Action ISource.Changed { add { Missing.Log(value); } remove { } }
            }
            """);
        EventForwarders(result);

        var run = Analyze(result, "ISource source = new Source(); Action handler = () => Shared.Total++; source.Changed += handler; source.Changed -= handler;");
        var gap = Assert.Single(run.Collection.Coverage.Gaps, gap => gap.Kind == SemanticGapKinds.UNKNOWN_LIBRARY &&
                                                                   gap.Callee == "Source.Changed__extern(Action)");
        Assert.Equal(2, gap.CallSites);
        Assert.Equal(2, gap.Sites.Count);
        var heap = run.Execution.Heap.Heap;
        var handoff = Assert.Single(heap.DelegateHandoffs);
        Assert.Equal(2, handoff.Sites.Count);
        Assert.Contains(handoff.Sites, site => heap.Instances[site.CallerInstance].BodyId.Contains("add_Changed", StringComparison.Ordinal));
        Assert.Contains(handoff.Sites, site => heap.Instances[site.CallerInstance].BodyId.Contains("remove_Changed", StringComparison.Ordinal));
        Assert.EndsWith("#lambda1", heap.Instances[Assert.Single(handoff.Callees)].BodyId, StringComparison.Ordinal);
    }

    private static IReadOnlyList<IMethodSymbol> EventForwarders(LibraryCompilationResult result)
    {
        Assert.True(result.Reason is null, $"{result.Reason}: {string.Join(", ", result.Errors)}");
        Assert.Empty(result.Compilation!.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var methods = new List<IMethodSymbol>();
        foreach (var tree in result.Compilation.SyntaxTrees)
        {
            var model = result.Compilation.GetSemanticModel(tree);
            foreach (var @event in tree.GetRoot().DescendantNodes().OfType<EventDeclarationSyntax>().Where(@event => @event.ExplicitInterfaceSpecifier is not null))
            {
                IMethodSymbol? forwarded = null;
                foreach (var accessor in @event.AccessorList!.Accessors)
                {
                    var statement = Assert.IsType<ExpressionStatementSyntax>(Assert.Single(accessor.Body!.Statements));
                    var call = Assert.IsType<InvocationExpressionSyntax>(statement.Expression);
                    Assert.Equal("value", Assert.Single(call.ArgumentList.Arguments).Expression.ToString());
                    var method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(call).Symbol);
                    Assert.True(method.IsExtern);
                    Assert.Equal(@event.Modifiers.Any(SyntaxKind.StaticKeyword), method.IsStatic);
                    var declaration = Assert.IsType<MethodDeclarationSyntax>(Assert.Single(method.DeclaringSyntaxReferences).GetSyntax());
                    Assert.Same(@event.Parent, declaration.Parent);
                    Assert.Null(declaration.Body);
                    Assert.Null(declaration.ExpressionBody);
                    Assert.Contains(method.GetDocumentationCommentId()!, result.ExternMembers);
                    Assert.Contains(model.GetDeclaredSymbol(accessor)!.GetDocumentationCommentId()!, result.ExternMembers);
                    if (forwarded is not null)
                        Assert.True(SymbolEqualityComparer.Default.Equals(forwarded, method));
                    forwarded = method;
                }
                methods.Add(forwarded!);
            }
        }
        return methods;
    }

    [Fact]
    public void A_custom_event_accessor_in_error_makes_a_field_like_extern_event()
    {
        var result = CompileSource("""
            using System;

            public sealed class Source
            {
                private Action _handlers;
                public event Action Changed { add { _handlers += value; Missing.Log(); } remove { _handlers -= value; } }
                public event Action Fine { add { _handlers += value; } remove { _handlers -= value; } }
            }
            """);

        Assert.Null(result.Reason);
        var tree = result.Compilation!.SyntaxTrees.Single();
        var changed = Assert.Single(tree.GetRoot().DescendantNodes().OfType<EventFieldDeclarationSyntax>());
        Assert.Contains(changed.Modifiers, modifier => modifier.IsKind(SyntaxKind.ExternKeyword));
        Assert.Equal("Changed", changed.Declaration.Variables.Single().Identifier.ValueText);
        Assert.Equal("Fine", Assert.Single(tree.GetRoot().DescendantNodes().OfType<EventDeclarationSyntax>()).Identifier.ValueText);
        Assert.Equal(["E:Source.Changed", "M:Source.add_Changed(System.Action)", "M:Source.remove_Changed(System.Action)"], result.ExternMembers);
        Assert.Equal(2, result.ExternBodies);
        Assert.Empty(result.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void A_constructor_in_error_loses_its_body_and_its_base_call_and_a_new_of_its_type_is_an_opaque_call()
    {
        var result = CompileSource("""
            public sealed class Ledger { public int Total; }

            public class Base
            {
                public Base(int start) { }
            }

            public sealed class Account : Base
            {
                public Account(Ledger ledger) : base(ledger.Total) { ledger.Total = "text"; }
            }
            """);

        Assert.Null(result.Reason);
        var account = result.Compilation!.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<ConstructorDeclarationSyntax>()
                            .Single(constructor => constructor.Identifier.ValueText == "Account");
        Assert.Null(account.Initializer);
        Assert.Null(account.Body);
        Assert.Contains(account.Modifiers, modifier => modifier.IsKind(SyntaxKind.ExternKeyword));
        Assert.Equal(["CS0824"], result.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
                                           .Select(diagnostic => diagnostic.Id).Distinct());
        Assert.Equal(["M:Account.#ctor(Ledger)"], result.ExternMembers);

        var run = Analyze(result, "_ = new Account(Shared);");
        Assert.Contains(run.Collection.Coverage.Gaps, gap => gap.Callee.StartsWith("Account.", StringComparison.Ordinal) && gap.Callee.EndsWith("(Ledger)", StringComparison.Ordinal) &&
                                                             gap.Kind == SemanticGapKinds.UNKNOWN_LIBRARY);
    }

    [Fact]
    public void An_initializer_error_makes_the_constructors_of_its_kind_extern_declaring_the_implicit_one()
    {
        var result = CompileSource("""
            public sealed class Settings
            {
                public static int Shared = 3;
                public int Retries = "three";
                public string Name { get; set; } = "name";
                public Settings(int retries) { Retries = retries; }
                public Settings() : this(1) { }
            }

            public sealed class Defaults
            {
                public int Value = Missing.Value;
            }

            public static class Registry
            {
                public static int Count = "text";
                public static int Kept;
                static Registry() { Kept = 1; }
            }
            """);

        Assert.Null(result.Reason);
        Assert.Equal(["M:Defaults.#ctor", "M:Registry.#cctor", "M:Settings.#ctor", "M:Settings.#ctor(System.Int32)"], result.ExternMembers);
        var root = result.Compilation!.SyntaxTrees.Single().GetRoot();
        var settings = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.ValueText == "Settings");
        Assert.All(settings.Members.OfType<ConstructorDeclarationSyntax>(), constructor => Assert.Null(constructor.Body));
        Assert.Null(settings.Members.OfType<PropertyDeclarationSyntax>().Single().Initializer);
        var fields = settings.Members.OfType<FieldDeclarationSyntax>().SelectMany(field => field.Declaration.Variables).ToDictionary(variable => variable.Identifier.ValueText);
        Assert.NotNull(fields["Shared"].Initializer);
        Assert.Null(fields["Retries"].Initializer);
        var defaults = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.ValueText == "Defaults");
        var declared = Assert.Single(defaults.Members.OfType<ConstructorDeclarationSyntax>());
        Assert.Contains(declared.Modifiers, modifier => modifier.IsKind(SyntaxKind.PublicKeyword));
        Assert.Contains(declared.Modifiers, modifier => modifier.IsKind(SyntaxKind.ExternKeyword));
        var registry = result.Compilation.GetTypeByMetadataName("Registry")!;
        Assert.True(registry.StaticConstructors.Single().IsExtern);
        Assert.Equal(3, result.ExternBodies);
        Assert.Empty(result.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void A_field_like_event_beside_its_backing_field_blocks_nothing()
    {
        var result = CompileSource("""
            using System;

            public sealed class Publisher
            {
                public event Action Changed;
                private Action Changed;
                public void Fine() { }
            }
            """);

        AssertBenign(result, "CS0102");
    }

    [Fact]
    public void An_equality_operator_without_its_inequality_blocks_nothing()
    {
        var result = CompileSource("""
            public sealed class Money
            {
                public static bool operator ==(Money left, Money right) => true;
                public override bool Equals(object other) => true;
                public override int GetHashCode() => 0;
            }
            """);

        AssertBenign(result, "CS0216");
    }

    [Fact]
    public void A_module_attribute_reserved_for_the_compiler_blocks_nothing()
    {
        var result = CompileSource("""
            [module: System.Runtime.CompilerServices.RefSafetyRules(11)]

            public sealed class Plain
            {
                public int Value() => 1;
            }
            """);

        AssertBenign(result, "CS8335");
    }

    [Fact]
    public void A_missing_type_in_a_signature_makes_the_library_not_compile()
    {
        var result = CompileSource("""
            public sealed class Api
            {
                public Missing Get() => null;
                public int Broken() { return "text"; }
            }
            """);

        Assert.Equal(GenerationReasons.LIBRARY_DOES_NOT_COMPILE, result.Reason);
        Assert.Null(result.Compilation);
        Assert.Contains("CS0246", result.Errors);
    }

    [Fact]
    public void A_library_is_compiled_once_per_path_and_reference_set()
    {
        using var install = new GenerationInstall();
        var assembly = Emitted(install, "Fixture.Once", ASYNC_SOURCE);

        var first = LibraryCompilation.Compile(assembly, CancellationToken.None);
        var second = LibraryCompilation.Compile(assembly with { MissingDependencies = ["ignored"] }, CancellationToken.None);

        Assert.Same(first, second);
        Assert.Same(first.Compilation, second.Compilation);
    }

    [Fact]
    public void The_same_path_under_two_frameworks_is_two_compilations()
    {
        using var install = new GenerationInstall();
        install.SharedFramework("8.0.1", runtime: true);
        install.SharedFramework("10.0.1", runtime: true);
        var package = install.Package("Fixture.Standard", "1.0.0");
        EmittedAssemblies.Write(Path.Combine(package, "lib", "netstandard2.0", "Fixture.Standard.dll"), "Fixture.Standard", ASYNC_SOURCE);
        var resolver = install.Resolver();

        var net8 = resolver.Resolve("Fixture.Standard", "1.0.0", "Fixture.Standard", "net8.0").Assembly!;
        var net10 = resolver.Resolve("Fixture.Standard", "1.0.0", "Fixture.Standard", "net10.0").Assembly!;
        var under8 = LibraryCompilation.Compile(net8, CancellationToken.None);
        var under10 = LibraryCompilation.Compile(net10, CancellationToken.None);

        Assert.Equal(net8.Path, net10.Path);
        Assert.NotSame(under8, under10);
        Assert.Null(under8.Reason);
        Assert.Null(under10.Reason);
        Assert.Contains(under8.Compilation!.References, reference => reference.Display!.Contains("8.0.1", StringComparison.Ordinal));
        Assert.Contains(under10.Compilation!.References, reference => reference.Display!.Contains("10.0.1", StringComparison.Ordinal));
        Assert.Same(under8, LibraryCompilation.Compile(resolver.Resolve("Fixture.Standard", "1.0", "Fixture.Standard", "net8.0").Assembly!, CancellationToken.None));
    }

    [Fact]
    public void Decompilation_stops_between_types()
    {
        using var install = new GenerationInstall();
        var assembly = Emitted(install, "Fixture.Between", """
            public sealed class First { public int Value() => 1; }
            public sealed class Second { public int Value() => 2; }
            """);
        using var cancellation = new CancellationTokenSource();
        var printed = new List<string>();
        // One worker takes the types in turn: the token fires once the first is printed, and the second's output never starts.
        var probe = new LibraryCompilationProbe
        {
            Workers = 1,
            TypePrinted = type =>
            {
                lock (printed)
                    printed.Add(type);
                cancellation.Cancel();
            }
        };

        Assert.ThrowsAny<OperationCanceledException>(() => LibraryCompilation.Compile(assembly, new HashSet<string>(StringComparer.Ordinal),
                                                                                      new GenerationChecks(cancellation.Token), probe));

        lock (printed)
            Assert.Single(printed);
    }

    [Fact]
    public async Task Waiting_for_a_compilation_another_caller_started_stops_with_the_waiters_token()
    {
        using var install = new GenerationInstall();
        var assembly = Emitted(install, "Fixture.Waiting", ASYNC_SOURCE);
        var none = new HashSet<string>(StringComparer.Ordinal);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var first = Task.Run(() => LibraryCompilation.Compile(assembly, none, new GenerationChecks(CancellationToken.None),
                                                              new LibraryCompilationProbe { Started = () => { held.Set(); release.Wait(); } }));
        Assert.True(held.Wait(TimeSpan.FromMinutes(1)));

        // The second caller finds the compilation under way and, just before it waits, its token fires.
        var second = Task.Run(() => LibraryCompilation.Compile(assembly, none, new GenerationChecks(cancellation.Token),
                                                               new LibraryCompilationProbe { Waiting = cancellation.Cancel }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        Assert.False(first.IsCompleted);
        release.Set();
        var result = await first;
        Assert.Null(result.Reason);
        Assert.Same(result, LibraryCompilation.Compile(assembly, CancellationToken.None));
    }

    /// <summary>The top-level types of an assembly's metadata the whole-project decompiler writes: all but <c>&lt;Module&gt;</c> and
    /// <c>&lt;PrivateImplementationDetails&gt;</c>, which carry nothing a project declares.</summary>
    /// <param name="path">The assembly file.</param>
    private static IReadOnlyList<string> TopLevelTypes(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var reader = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        return reader.TypeDefinitions.Select(reader.GetTypeDefinition)
                     .Where(type => type.GetDeclaringType().IsNil)
                     .Select(type => (Namespace: reader.GetString(type.Namespace), Name: reader.GetString(type.Name)))
                     .Where(type => type.Name is not ("<Module>" or "<PrivateImplementationDetails>"))
                     .Select(type => type.Namespace.Length > 0 ? $"{type.Namespace}.{type.Name}" : type.Name)
                     .ToArray();
    }

    private static string FileName(string typeName) => string.Concat(typeName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static void AssertBenign(LibraryCompilationResult result, string code)
    {
        Assert.True(result.Reason is null, $"{result.Reason}: {string.Join(", ", result.Errors)}");
        Assert.Equal(0, result.ExternBodies);
        Assert.Empty(result.ExternMembers);
        Assert.Contains(result.Compilation!.GetDiagnostics(), diagnostic => diagnostic.Id == code && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void A_type_the_decompiler_threw_on_makes_the_whole_library_unusable_and_is_named()
    {
        var tree = CSharpSyntaxTree.ParseText("namespace Lib { public static class Api { public static void Run(System.Action a) => a(); } }",
                                              new CSharpParseOptions(LanguageVersion.Preview), "Fixture.Library/Lib.Api.cs");
        var failed = new DecompiledModule([tree], ["Lib.Broken: InvalidOperationException: the decompiler gave up"]);

        var refused = LibraryCompilation.CompileModule("Fixture.Library", failed, EmittedAssemblies.RuntimeReferences, CancellationToken.None);
        var whole = LibraryCompilation.CompileModule("Fixture.Library", failed with { Failures = [] }, EmittedAssemblies.RuntimeReferences,
                                                     CancellationToken.None);
        var answer = ModelGenerator.Trace(new GenerationRequest("Fixture.Library", "1.0", "M:Lib.Api.Run(System.Action)", null, null), refused,
                                          CancellationToken.None).Answer;

        Assert.Null(refused.Compilation);
        Assert.Equal(GenerationReasons.LIBRARY_DOES_NOT_COMPILE, refused.Reason);
        Assert.Equal(["decompiler: Lib.Broken: InvalidOperationException: the decompiler gave up"], refused.Errors);
        // The member asked for compiled; the library is refused anyway, so no member is classified against a partial library.
        Assert.Equal(GenerationReasons.LIBRARY_DOES_NOT_COMPILE, answer.Reason);
        Assert.Contains("Lib.Broken", answer.Detail, StringComparison.Ordinal);
        Assert.Null(whole.Reason);
        Assert.NotNull(whole.Compilation!.GetTypeByMetadataName("Lib.Api"));
    }

    private static IMethodSymbol Method(LibraryCompilationResult result, string type, string name) =>
        result.Compilation!.GetTypeByMetadataName(type)!.GetMembers(name).OfType<IMethodSymbol>().Single();

    private static LibraryCompilationResult CompileSource(string source) =>
        LibraryCompilation.CompileTrees("Fixture.Library",
                                        [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), "Fixture.Library/Library.cs")],
                                        EmittedAssemblies.RuntimeReferences, CancellationToken.None);

    /// <summary>Runs the engine over the rewritten library and a controller whose action does <paramref name="work"/> on a shared
    /// <c>Ledger</c>.</summary>
    /// <param name="result">The compiled library.</param>
    /// <param name="work">The action's statement.</param>
    private static Engine.EngineRun Analyze(LibraryCompilationResult result, string work) =>
        AnalyzeScope(FixtureSolution.Create(("Library.cs", result.Compilation!.SyntaxTrees.Single().ToString()), ("Case.cs", Usings + $$"""
            public sealed class LedgerController : ControllerBase
            {
                private static readonly Ledger Shared = new();

                [HttpPost("/ledger")]
                public void Post() { {{work}} }
            }
            """ + Startup())), "scope:Fixture");

    /// <summary>An assembly emitted from source into an install, as the resolver would describe it, against the running runtime.</summary>
    /// <param name="install">The install the file lives in.</param>
    /// <param name="name">The assembly name.</param>
    /// <param name="source">Its source.</param>
    private static ImplementationAssembly Emitted(GenerationInstall install, string name, string source)
    {
        var path = EmittedAssemblies.Write(Path.Combine(install.Root, name, name + ".dll"), name, source);
        var runtime = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        return new ImplementationAssembly(name, path, null, "1.0.0", $"net{Environment.Version.Major}.0", runtime,
                                          EmittedAssemblies.RUNTIME_ASSEMBLIES.Select(reference => Path.Combine(runtime, reference + ".dll")).ToArray(),
                                          [], "0.0.0.0", "", Guid.Empty);
    }

    private sealed record LinqLibrary(ImplementationAssembly Assembly, DecompiledModule Module);
}
