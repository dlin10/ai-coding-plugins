using Common.Roslyn;
using ConcurrencyHunter.Core.Tests.Expectations;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibrarySemantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

/// <summary>Rule 3 of the demo catalog for the cases of phase 5a (R6): a case of a phase holds no call a later phase would make a
/// semantic gap of. Every call of a member without source in their files is a known call, or belongs to the frame every demo case
/// shares: the hosting, DI and ASP.NET Core abstractions and <c>Task</c>, <c>Task&lt;TResult&gt;</c> and <c>CancellationToken</c>.</summary>
public sealed class DemoCaseRuleTests
{
    private static readonly string[] FRAME_TYPES =
        ["System.Threading.Tasks.Task", "System.Threading.Tasks.Task`1", "System.Threading.CancellationToken"];

    [Fact]
    public async Task Phase_5a_cases_make_no_gap_calls()
    {
        var expectations = ExpectationFile.Load(RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "expected-findings.json"));
        var cases = expectations.Findings.Where(entry => entry.Phase == "5a").Select(entry => entry.Id)
                                .Concat(expectations.NotDefects.Where(entry => entry.Phase == "5a").Select(entry => entry.Id))
                                .Select(id => Pascal(id.Split('/')[0]))
                                .Distinct(StringComparer.Ordinal)
                                .ToArray();
        Assert.NotEmpty(cases);

        await DemoWorkspace.EnsureRestoredAsync();
        using var loaded = await new MsBuildSolutionLoader().LoadAsync(RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "demo", "Demo.slnx"));
        var violations = new List<string>();
        foreach (var name in cases)
        {
            var document = Assert.Single(loaded.Solution.Projects.SelectMany(project => project.Documents),
                                         candidate => candidate.FilePath is { } path && Path.GetFileName(path) == name + ".cs" &&
                                                      path.Contains($"{Path.DirectorySeparatorChar}Cases{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
            var model = (await document.GetSemanticModelAsync())!;
            foreach (var method in Calls(model).Where(method => !method.Locations.Any(location => location.IsInSource)))
            {
                if (LibrarySemanticsTable.BuiltIn.Find(method) is not { Kind: LibraryMatchKind.Known } && !IsFrame(method))
                    violations.Add($"{name}.cs calls {method.ToDisplayString()} of {method.ContainingAssembly.Identity}, neither known nor frame");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Calls_names_setters_and_the_base_constructor_of_a_primary_constructor()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Options { void M() { var options = new System.Text.Json.JsonSerializerOptions(); options.WriteIndented = true; options.MaxDepth += 1; } }
            class Client() : System.Net.Http.HttpClient { }
            class Handled(System.Net.Http.HttpMessageHandler handler) : System.Net.Http.HttpClient(handler) { }
            """);
        var compilation = CSharpCompilation.Create("Rule", [tree], StubAssemblies.PlatformWithout([]),
                                                   new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var calls = Calls(compilation.GetSemanticModel(tree)).Select(method => method.ToDisplayString()).ToArray();

        Assert.Contains("System.Text.Json.JsonSerializerOptions.WriteIndented.set", calls);
        Assert.Contains("System.Text.Json.JsonSerializerOptions.MaxDepth.get", calls);
        Assert.Contains("System.Text.Json.JsonSerializerOptions.MaxDepth.set", calls);
        Assert.DoesNotContain("System.Text.Json.JsonSerializerOptions.WriteIndented.get", calls);
        Assert.Contains("System.Net.Http.HttpClient.HttpClient()", calls);
        Assert.Contains("System.Net.Http.HttpClient.HttpClient(System.Net.Http.HttpMessageHandler)", calls);
    }

    /// <summary>Every member a file calls: invocations, object creations, the getters and setters of properties, and field
    /// initializers, in every body and lambda, and the base constructor each constructor calls, the implicit one of a type that
    /// declares none and the one a primary constructor calls included.</summary>
    private static IEnumerable<IMethodSymbol> Calls(SemanticModel model)
    {
        var root = model.SyntaxTree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            var operation = node switch
            {
                BaseMethodDeclarationSyntax or AccessorDeclarationSyntax => model.GetOperation(node),
                ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax } => model.GetOperation(node),
                EqualsValueClauseSyntax { Parent.Parent.Parent: FieldDeclarationSyntax or PropertyDeclarationSyntax } => model.GetOperation(node),
                EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax } => model.GetOperation(node),
                PrimaryConstructorBaseTypeSyntax => model.GetOperation(node),
                _ => null
            };
            foreach (var called in operation?.DescendantsAndSelf() ?? [])
            {
                IEnumerable<IMethodSymbol?> methods = called switch
                {
                    IInvocationOperation invocation => [invocation.TargetMethod],
                    IObjectCreationOperation creation => [creation.Constructor],
                    IPropertyReferenceOperation property => Accessors(property),
                    _ => []
                };
                foreach (var method in methods.OfType<IMethodSymbol>())
                    yield return method;
            }
        }

        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(declaration => model.GetDeclaredSymbol(declaration)).OfType<INamedTypeSymbol>())
        {
            foreach (var constructor in type.InstanceConstructors.Where(constructor =>
                         constructor.IsImplicitlyDeclared ||
                         constructor.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() switch
                         {
                             ConstructorDeclarationSyntax declaration => declaration.Initializer is null,
                             // A primary constructor whose base type is given no arguments calls the parameterless base constructor.
                             TypeDeclarationSyntax declaration => declaration.BaseList?.Types.FirstOrDefault() is not PrimaryConstructorBaseTypeSyntax,
                             _ => false
                         })))
            {
                if (type.BaseType?.InstanceConstructors.FirstOrDefault(candidate => candidate.Parameters.Length == 0) is { } baseConstructor)
                    yield return baseConstructor;
            }
        }
    }

    /// <summary>The accessors a property reference calls: the setter of an assignment's target, both of a compound assignment's
    /// or an increment's, and the getter of anything else.</summary>
    private static IEnumerable<IMethodSymbol?> Accessors(IPropertyReferenceOperation property) => property.Parent switch
    {
        ISimpleAssignmentOperation assignment when assignment.Target == property => [property.Property.SetMethod],
        IAssignmentOperation assignment when assignment.Target == property => [property.Property.GetMethod, property.Property.SetMethod],
        IIncrementOrDecrementOperation step when step.Target == property => [property.Property.GetMethod, property.Property.SetMethod],
        _ => [property.Property.GetMethod]
    };

    private static bool IsFrame(IMethodSymbol method)
    {
        var assembly = method.ContainingAssembly.Identity.Name;
        return assembly is "Microsoft.Extensions.Hosting.Abstractions" or "Microsoft.Extensions.DependencyInjection.Abstractions" ||
               assembly.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) ||
               FRAME_TYPES.Contains(MetadataName(method.ContainingType.OriginalDefinition), StringComparer.Ordinal);
    }

    private static string MetadataName(INamedTypeSymbol type) => $"{type.ContainingNamespace.ToDisplayString()}.{type.MetadataName}";

    private static string Pascal(string kebab) => string.Concat(kebab.Split('-').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
