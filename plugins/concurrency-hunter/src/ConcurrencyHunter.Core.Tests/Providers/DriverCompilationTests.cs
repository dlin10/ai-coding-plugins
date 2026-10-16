using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The driver compiles against the opened copy of the library, which names the driver's assembly a friend: overrides of
/// <c>protected internal</c> members, delegate seeds whose signature names an internal type, and members with a pointer in their
/// signature (SPEC TD-034b, «Драйвер»).</summary>
public sealed class DriverCompilationTests
{
    private const string SOURCE = """
        using System;

        namespace Lib
        {
            public abstract class Scheduler
            {
                protected Scheduler() { }
                protected internal abstract void Queue(object item);
                protected internal virtual void Notify(object sender) { }
            }

            public abstract class Gauge
            {
                protected Gauge() { }
                public virtual object Level { get => null; protected internal set { } }
            }

            internal sealed class Info { public object Value; }
            public unsafe delegate object Raw(object target, IntPtr* arguments);

            public static class Api
            {
                internal static Action<Info> Changed;
                internal static Raw Handler;

                public static void Schedule(Scheduler scheduler, Action done) { scheduler.Queue(done); }
                public static void Measure(Gauge gauge, Action done) { done(); }
                public static void Watch(Action done) { done(); Changed?.Invoke(new Info()); }
                public static unsafe void Call(Action done) { done(); Handler?.Invoke(done, null); }
                public static unsafe int* Pin(Action done) { done(); return null; }
                public static unsafe bool Raw(Action done, out byte* blob) { done(); blob = null; return true; }
            }
        }
        """;

    private static readonly Lazy<LibraryCompilationResult> Opened = new(() => Compile(openFields: true));
    private static readonly Lazy<LibraryCompilationResult> Closed = new(() => Compile(openFields: false));

    [Fact]
    public void An_override_of_a_protected_internal_member_stays_protected_internal_for_a_friend()
    {
        var probe = Probe(Drive("M:Lib.Api.Schedule(Lib.Scheduler,System.Action)", Opened.Value), "Lib.Scheduler");
        var gauge = Probe(Drive("M:Lib.Api.Measure(Lib.Gauge,System.Action)", Opened.Value), "Lib.Gauge");

        Assert.Equal(Accessibility.ProtectedOrInternal, Member<IMethodSymbol>(probe, "Queue").DeclaredAccessibility);
        Assert.Equal(Accessibility.ProtectedOrInternal, Member<IMethodSymbol>(probe, "Notify").DeclaredAccessibility);
        Assert.Equal(Accessibility.ProtectedOrInternal, Member<IPropertySymbol>(gauge, "Level").SetMethod!.DeclaredAccessibility);
    }

    [Fact]
    public void An_override_of_a_protected_internal_member_is_protected_without_the_friend()
    {
        var probe = Probe(Drive("M:Lib.Api.Schedule(Lib.Scheduler,System.Action)", Closed.Value), "Lib.Scheduler");

        Assert.Equal(Accessibility.Protected, Member<IMethodSymbol>(probe, "Queue").DeclaredAccessibility);
        Assert.Equal(Accessibility.Protected, Member<IMethodSymbol>(probe, "Notify").DeclaredAccessibility);
    }

    [Fact]
    public void A_delegate_seed_whose_signature_names_an_internal_type_compiles()
    {
        var driver = Drive("M:Lib.Api.Watch(System.Action)", Opened.Value);

        Assert.Contains(driver.SeedStatements, seed => seed.Path == "static/F:Lib.Api.Changed");
    }

    [Fact]
    public void A_delegate_seed_with_a_pointer_in_its_signature_compiles()
    {
        var driver = Drive("M:Lib.Api.Call(System.Action)", Opened.Value);

        Assert.Contains(driver.SeedStatements, seed => seed.Path == "static/F:Lib.Api.Handler");
    }

    [Fact]
    public void A_member_with_a_pointer_in_its_signature_is_called()
    {
        Drive("M:Lib.Api.Pin(System.Action)", Opened.Value);
    }

    [Fact]
    public void A_member_with_an_out_pointer_is_called()
    {
        Drive("M:Lib.Api.Raw(System.Action,System.Byte*@)", Opened.Value);
    }

    private static INamedTypeSymbol Probe(Driver driver, string baseType) =>
        driver.Compilation.Assembly.GlobalNamespace.GetTypeMembers()
              .Single(type => type.BaseType?.ToDisplayString() == baseType);

    private static T Member<T>(INamedTypeSymbol type, string name) where T : ISymbol =>
        type.GetMembers(name).OfType<T>().Single();

    private static Driver Drive(string id, LibraryCompilationResult library)
    {
        Assert.NotNull(library.Compilation);
        var trace = ModelGenerator.Trace(new GenerationRequest("Fixture.Library", "1.0", id, null, null), library, CancellationToken.None);
        Assert.True(trace.Driver is not null, $"{trace.Answer.Reason}: {trace.Answer.Detail}");
        Assert.Empty(trace.Driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return trace.Driver;
    }

    private static LibraryCompilationResult Compile(bool openFields) =>
        LibraryCompilation.CompileTrees("Fixture.Library",
                                        [CSharpSyntaxTree.ParseText(SOURCE, new CSharpParseOptions(LanguageVersion.Preview), "Fixture.Library/Library.cs")],
                                        EmittedAssemblies.RuntimeReferences, CancellationToken.None, openFields: openFields);
}
