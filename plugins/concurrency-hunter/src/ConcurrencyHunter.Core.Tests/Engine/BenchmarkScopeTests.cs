using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class BenchmarkScopeTests
{
    private const string SOURCE = """
        using System;
        public interface IContract { int Value { get; } void Run(); event Action Changed; }
        public interface IDefault { int Compute() => 1; int Value => 2; }
        public class Base : IDisposable
        {
            public override string ToString() => "base";
            public override bool Equals(object other) => false;
            public override int GetHashCode() => 0;
            public void Dispose() { }
            public virtual int Value { get; set; }
            public virtual event Action Changed { add { } remove { } }
            public virtual void Run() { }
        }
        public class Derived : Base, IContract
        {
            public override string ToString() => "derived";
            public override int Value { get => 1; set { } }
            public override event Action Changed { add { } remove { } }
            public override void Run() { }
            public void Plain() { }
        }
        public class Explicit : IDisposable, IContract
        {
            void IDisposable.Dispose() { }
            int IContract.Value => 1;
            void IContract.Run() { }
            event Action IContract.Changed { add { } remove { } }
        }
        public abstract class Abstract : Base
        {
            public abstract override void Run();
            public abstract override int Value { get; set; }
            public extern override string ToString();
        }
        public class Auto : IContract
        {
            public int Value { get; set; }
            public void Run() { }
            public event Action Changed, Unrelated;
        }
        """;

    internal static CSharpCompilation Compilation() =>
        CSharpCompilation.Create("BenchmarkFixture", [CSharpSyntaxTree.ParseText(SOURCE)], EmittedAssemblies.RuntimeReferences,
                                  new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    [Fact]
    public void Four_takes_overrides_of_the_three_object_members_through_an_override_chain()
    {
        var selected = BenchmarkScopes.Select(Compilation(), "four");
        Assert.Contains("M:Base.ToString", selected);
        Assert.Contains("M:Derived.ToString", selected);
        Assert.Contains("M:Base.Equals(System.Object)", selected);
        Assert.Contains("M:Base.GetHashCode", selected);
    }

    [Fact]
    public void Four_takes_dispose_implementations_implicit_explicit_and_inherited()
    {
        var compilation = Compilation();
        var selected = BenchmarkScopes.Select(compilation, "four");
        Assert.Contains("M:Base.Dispose", selected);
        Assert.Contains("M:Explicit.System#IDisposable#Dispose", selected);
        var derived = compilation.GetTypeByMetadataName("Derived")!;
        var dispose = derived.FindImplementationForInterfaceMember(compilation.GetSpecialType(SpecialType.System_IDisposable).GetMembers("Dispose").Single())!;
        Assert.Contains(dispose.GetDocumentationCommentId()!, selected);
    }

    [Fact]
    public void All_takes_every_override_and_every_interface_implementation()
    {
        var selected = BenchmarkScopes.Select(Compilation(), "all");
        Assert.Contains("M:Derived.Run", selected);
        Assert.Contains("P:Derived.Value", selected);
        Assert.Contains("E:Derived.Changed", selected);
        Assert.Contains("M:Explicit.IContract#Run", selected);
        Assert.Contains("P:Explicit.IContract#Value", selected);
        Assert.Contains("P:Auto.Value", selected);
        Assert.Contains("E:Auto.Changed", selected);
        Assert.Contains("M:Base.Dispose", selected);
    }

    [Fact]
    public void Property_implementing_through_its_getter_is_selected_whole()
    {
        var selected = BenchmarkScopes.Select(Compilation(), "all");
        Assert.Contains("P:Auto.Value", selected);
        Assert.DoesNotContain("M:Auto.get_Value", selected);
        Assert.DoesNotContain("M:Auto.set_Value(System.Int32)", selected);
    }

    [Fact]
    public void Explicit_interface_event_is_not_selected() =>
        Assert.DoesNotContain("E:Explicit.IContract#Changed", BenchmarkScopes.Select(Compilation(), "all"));

    [Fact]
    public void Member_without_a_body_is_not_selected()
    {
        foreach (var scope in new[] { "four", "all" })
        {
            var selected = BenchmarkScopes.Select(Compilation(), scope);
            Assert.DoesNotContain("M:Abstract.Run", selected);
            Assert.DoesNotContain("P:Abstract.Value", selected);
            Assert.DoesNotContain("M:Abstract.ToString", selected);
            Assert.DoesNotContain("M:IContract.Run", selected);
        }
    }

    [Fact]
    public void Default_interface_implementation_is_selected()
    {
        var selected = BenchmarkScopes.Select(Compilation(), "all");
        Assert.Contains("M:IDefault.Compute", selected);
        Assert.Contains("P:IDefault.Value", selected);
    }

    [Fact]
    public void All_selection_of_the_fixture_compiles_without_an_error()
    {
        var compilation = Compilation();
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var result = LibraryCompilation.CompileTrees(compilation.AssemblyName!, compilation.SyntaxTrees.ToArray(), compilation.References.ToArray(),
                                                    CancellationToken.None, BenchmarkScopes.Select(compilation, "all"));
        Assert.Null(result.Reason);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Compilation!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.False(result.Compilation.GetTypeByMetadataName("Auto")!.GetMembers("Unrelated").Single().IsExtern);
    }

    [Fact]
    public void Member_that_neither_overrides_nor_implements_is_not_selected()
    {
        foreach (var scope in new[] { "four", "all" })
        {
            var selected = BenchmarkScopes.Select(Compilation(), scope);
            Assert.DoesNotContain("M:Derived.Plain", selected);
            Assert.DoesNotContain("E:Auto.Unrelated", selected);
            Assert.DoesNotContain("M:Base.Run", selected);
        }
    }
}
