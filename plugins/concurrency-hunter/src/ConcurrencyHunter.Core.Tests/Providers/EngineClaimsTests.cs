using System.Runtime.InteropServices;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The engine's claims by name outside the models (SPEC TD-034b, G-4): an implementation assembly declaring a type a
/// recognizer claims is refused before the pipeline runs.</summary>
public sealed class EngineClaimsTests
{
    private const string MEMBER = "M:Lib.Api.Run(System.Action)";

    [Theory]
    [InlineData("collections", "System.Collections.Generic", "LinkedList<T>")]
    [InlineData("spawn", "System.Threading.Tasks", "Parallel")]
    [InlineData("timer", "System.Timers", "Timer")]
    [InlineData("lock", "System.Threading", "ReaderWriterLockSlim")]
    [InlineData("service-call", "Microsoft.Extensions.DependencyInjection", "IServiceScopeFactory")]
    [InlineData("recognized", "Microsoft.Extensions.DependencyInjection", "IServiceScope")]
    [InlineData("di-registration", "Microsoft.Extensions.DependencyInjection", "ServiceCollectionServiceExtensions")]
    public void A_library_declaring_a_type_a_recognizer_claims_is_engine_recognized(string recognizer, string ns, string type) =>
        AssertRefused(recognizer, ns, type);

    [Fact]
    public void The_collection_table_claims_its_types() => AssertRefused("collections", "System.Collections.Concurrent", "ConcurrentDictionary<TKey, TValue>");

    [Fact]
    public void The_spawn_recognizer_claims_its_types() => AssertRefused("spawn", "System.Threading", "ThreadPool");

    [Fact]
    public void The_timer_recognizer_claims_its_types() => AssertRefused("timer", "System.Threading", "Timer");

    [Fact]
    public void The_lock_recognizer_claims_its_types() => AssertRefused("lock", "System.Threading", "SemaphoreSlim");

    [Fact]
    public void The_service_call_recognizer_claims_its_types() => AssertRefused("service-call", "Microsoft.Extensions.DependencyInjection", "ServiceProviderServiceExtensions");

    [Fact]
    public void The_types_whose_calls_are_never_unresolved_are_claimed() => AssertRefused("recognized", "System.Threading", "Interlocked");

    [Fact]
    public void The_DI_registrations_are_claimed() =>
        AssertRefused("di-registration", "Microsoft.Extensions.DependencyInjection.Extensions", "ServiceCollectionDescriptorExtensions");

    [Fact]
    public void A_nested_type_is_claimed_by_its_outermost_type()
    {
        var library = Compile(Library("System.Collections.Generic", "Dictionary<TKey, TValue>", "public sealed class KeyCollection { public int Count() => 0; }"));
        var nested = library.Compilation!.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2+KeyCollection")!.GetMembers("Count")
                            .OfType<IMethodSymbol>().Single();

        Assert.Equal("collections", EngineClaims.Of(nested));
    }

    [Fact]
    public void A_library_of_unclaimed_types_runs_on()
    {
        var library = Compile(Library("Lib.Things", "Parallel", ""));

        Assert.Null(EngineClaims.FirstIn(library.Compilation!.Assembly));
        Assert.Null(ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", MEMBER, null, null), library, CancellationToken.None).Answer.Reason);
    }

    [Fact]
    public void The_installed_runtimes_System_Linq_is_claimed_by_none()
    {
        var path = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.Linq.dll");
        var compilation = CSharpCompilation.Create("Probe", [], [.. EmittedAssemblies.RuntimeReferences, MetadataReference.CreateFromFile(path)],
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, metadataImportOptions: MetadataImportOptions.All));
        var linq = compilation.SourceModule.ReferencedAssemblySymbols.Single(assembly => assembly.Name == "System.Linq");

        Assert.Contains(linq.GlobalNamespace.GetNamespaceMembers(), ns => ns.Name == "System");
        Assert.Null(EngineClaims.FirstIn(linq));
    }

    private static void AssertRefused(string recognizer, string ns, string type)
    {
        var library = Compile(Library(ns, type, "public static void Touch() { }"));
        var claimed = library.Compilation!.GetTypeByMetadataName($"{ns}.{MetadataName(type)}")!.GetMembers("Touch").OfType<IMethodSymbol>().Single();

        var answer = ModelGenerator.Trace(new GenerationRequest(ASSEMBLY, "1.0", MEMBER, null, null), library, CancellationToken.None).Answer;

        Assert.Equal(recognizer, EngineClaims.Of(claimed));
        Assert.Null(answer.Classified);
        Assert.Equal(GenerationReasons.ENGINE_RECOGNIZED, answer.Reason);
        Assert.Contains($"the {recognizer} recognizer", answer.Detail);
        Assert.Equal(0, answer.Generation.ReachedBodies);
    }

    /// <summary>A library with the delegate-taking member asked for and one type of a namespace and name a recognizer may claim.</summary>
    /// <param name="ns">The type's namespace.</param>
    /// <param name="type">The type's name, with its type parameters.</param>
    /// <param name="members">The type's members.</param>
    private static string Library(string ns, string type, string members) => $$"""
        namespace Lib { public static class Api { public static void Run(System.Action a) { a(); } } }
        namespace {{ns}} { public class {{type}} { {{members}} } }
        """;

    private static string MetadataName(string type) =>
        type.IndexOf('<') is var open and >= 0 ? $"{type[..open]}`{type.Count(character => character == ',') + 1}" : type;
}
