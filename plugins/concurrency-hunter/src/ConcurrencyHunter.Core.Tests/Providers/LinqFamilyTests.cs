using System.Text.RegularExpressions;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The built-in LINQ family (R6) against the .NET 10 reference assembly: every <c>Enumerable</c> operator taking a delegate and
/// no comparer is described by the LINQ table, and the phase 5a members R6 names return what .NET returns.</summary>
public sealed class LinqFamilyTests
{
    private const string LINQ_RANGE = "System.Linq@8.0.0.0-11.0.0.0";

    private static readonly Lazy<CSharpCompilation> Reference = new(() => CSharpCompilation.Create(
        "Linq", [], StubAssemblies.PlatformWithout([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

    [Fact]
    public void Every_Enumerable_member_with_a_delegate_and_no_comparer_is_modelled()
    {
        var family = Operators().Where(method => TakesADelegate(method) && !TakesAComparer(method)).ToArray();
        var fated = LibraryModels.BuiltIn.Members.Where(member => member.Id.StartsWith("M:System.Linq.Enumerable.", StringComparison.Ordinal) &&
                                                                  member.Fates.Count != 0);

        Assert.Equal(93, family.Length);
        Assert.All(family, method =>
        {
            var model = Assert.Single(LibraryModels.BuiltIn.Members, member => member.Id == Id(method));
            Assert.Equal(LINQ_RANGE, Ranges(model));
            Assert.Equal(method.Parameters.Where(parameter => parameter.Type.TypeKind == TypeKind.Delegate).Select(parameter => parameter.Name)
                               .Order(StringComparer.Ordinal),
                         model.Fates.Select(fate => fate.Parameter).Order(StringComparer.Ordinal));
        });
        Assert.Equal(family.Select(Id).Order(StringComparer.Ordinal), fated.Select(member => member.Id).Order(StringComparer.Ordinal));
        // Every one of them is checked by exactly one row of the LINQ table.
        var theory = typeof(LinqFamilyTests).GetMethod(nameof(Operator_semantics))!;
        var covered = theory.GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>()
                            .SelectMany(data => ((string)data.GetData(theory).Single()[1]).Split(' ').SelectMany(Overloads))
                            .Select(Id)
                            .ToArray();
        Assert.Equal(family.Select(Id).Order(StringComparer.Ordinal), covered.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void No_Enumerable_member_with_a_comparer_is_modelled()
    {
        var withComparer = Operators().Where(TakesAComparer).ToArray();
        var ids = LibraryModels.BuiltIn.Members.Select(member => member.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(withComparer, method => method.Name == "CountBy");
        Assert.Contains(withComparer, method => method.Name == "AggregateBy");
        Assert.Contains(withComparer, method => method.Name == "OrderBy");
        Assert.All(withComparer, method =>
        {
            Assert.DoesNotContain(Id(method), ids);
            Assert.Null(LibraryModels.BuiltIn.Find(method));
        });
    }

    [Fact]
    public void Every_input_and_result_names_existing_parameters()
    {
        var described = LibraryModels.BuiltIn.Members.Where(member => member.Id.StartsWith("M:System.Linq.Enumerable.", StringComparison.Ordinal) &&
                                                                      (member.Fates.Count != 0 || member.Result is not null)).ToArray();

        Assert.Equal(93 + 15, described.Length);
        Assert.All(described, member =>
        {
            var method = Resolve(member.Id);
            var fated = member.Fates.Select(fate => fate.Parameter).ToHashSet(StringComparer.Ordinal);
            var texts = member.Fates.SelectMany(fate => fate.Inputs ?? []).SelectMany(input => input).Select(value => value.ToString())
                              .Append(member.Result?.ToString() ?? "");
            foreach (var reference in texts.SelectMany(text => Regex.Matches(text, @"(arg|returns):(\w+)")))
            {
                var parameter = reference.Groups[2].Value;
                Assert.Contains(method.Parameters, candidate => candidate.Name == parameter);
                if (reference.Groups[1].Value == "returns")
                    Assert.Contains(parameter, fated);
            }
            Assert.All(member.Fates, fate =>
            {
                var type = Assert.IsAssignableFrom<INamedTypeSymbol>(Assert.Single(method.Parameters, parameter => parameter.Name == fate.Parameter).Type);
                Assert.Equal(type.DelegateInvokeMethod!.Parameters.Length, fate.Inputs?.Count);
            });
        });
    }

    [Fact]
    public void Lazy_phase_5a_members_are_sequences_of_their_sources()
    {
        var lazy = new (string Name, string Result, string Effects)[]
        {
            ("Skip", "sequence(elements(arg:source))", "reads-deep:source"),
            ("Take", "sequence(elements(arg:source))", "reads-deep:source"),
            ("Cast", "sequence(elements(arg:source))", "reads-deep:source"),
            ("OfType", "sequence(elements(arg:source))", "reads-deep:source"),
            ("Union", "sequence(elements(arg:first),elements(arg:second))", "reads-deep:first,reads-deep:second")
        };

        var members = lazy.SelectMany(entry => Phase5a(entry.Name).Select(member => (entry, member))).ToArray();

        Assert.Equal(6, members.Length);
        Assert.All(members, pair =>
        {
            Assert.Equal(pair.entry.Result, pair.member.Result?.ToString());
            Assert.Equal(pair.entry.Effects, Effects(pair.member));
            Assert.Empty(pair.member.Fates);
        });
    }

    [Fact]
    public void AsEnumerable_returns_its_source()
    {
        var member = Assert.Single(Phase5a("AsEnumerable"));

        Assert.Equal("[arg:source]", member.Result?.ToString());
        Assert.Equal("reads-deep:source", Effects(member));
    }

    [Fact]
    public void Delegate_free_default_value_overloads_stay_unmodelled()
    {
        var names = new[] { "FirstOrDefault", "LastOrDefault", "SingleOrDefault" };
        var overloads = Operators().Where(method => names.Contains(method.Name) && !TakesADelegate(method) &&
                                                     method.Parameters.Any(parameter => parameter.Name == "defaultValue")).ToArray();

        Assert.Equal(3, overloads.Length);
        Assert.All(overloads, method =>
        {
            Assert.DoesNotContain(LibraryModels.BuiltIn.Members, member => member.Id == Id(method));
            Assert.Null(LibraryModels.BuiltIn.Find(method));
        });
        // The source-only overloads of phase 5a beside them return one of the source's elements.
        Assert.All(names.SelectMany(Phase5a), member => Assert.Equal("[elements(arg:source)]", member.Result?.ToString()));
    }

    /// <summary>One row per row of the LINQ table. <paramref name="members"/> selects the overloads without a comparer that take a
    /// delegate: a name, a name with its type parameter count after two backticks, or a name with its exact parameter names.
    /// <paramref name="inputs"/> gives each delegate parameter its inputs, <c>*</c> every one; an <c>int</c> index parameter the row
    /// leaves out gets <c>[]</c>. <c>E(p)</c> is <c>elements(arg:p)</c>; <c>arg:defaultValue</c> counts only where the overload has it.
    /// A delegate is <c>iterator</c> when the result is a sequence and <c>invoke-now</c> otherwise.</summary>
    [Theory]
    [InlineData("All, Any, Count, LongCount", "All Any Count LongCount", "*:[E(source)]", "", "")]
    [InlineData("Average, Sum, Min/Max over numbers", "Average Sum Min``1 Max``1", "*:[E(source)]", "", "")]
    [InlineData("Min/Max <TSource, TResult>", "Min``2 Max``2", "*:[E(source)]", "[returns:selector]", "")]
    [InlineData("MinBy, MaxBy", "MinBy MaxBy", "*:[E(source)]", "[E(source)]", "")]
    [InlineData("First, FirstOrDefault, Last, LastOrDefault, Single, SingleOrDefault",
                "First FirstOrDefault Last LastOrDefault Single SingleOrDefault", "*:[E(source)]", "[E(source),arg:defaultValue]", "")]
    [InlineData("Aggregate(source, func)", "Aggregate(source,func)", "func:[E(source),returns:func][E(source)]", "[E(source),returns:func]", "")]
    [InlineData("Aggregate(source, seed, func)", "Aggregate(source,seed,func)", "func:[arg:seed,returns:func][E(source)]", "[arg:seed,returns:func]", "")]
    [InlineData("Aggregate(source, seed, func, resultSelector)", "Aggregate(source,seed,func,resultSelector)",
                "func:[arg:seed,returns:func][E(source)];resultSelector:[arg:seed,returns:func]", "[returns:resultSelector]", "")]
    [InlineData("ToDictionary(source, keySelector)", "ToDictionary(source,keySelector)", "keySelector:[E(source)]",
                "dictionary(returns:keySelector,E(source))", "")]
    [InlineData("ToDictionary(source, keySelector, elementSelector)", "ToDictionary(source,keySelector,elementSelector)", "*:[E(source)]",
                "dictionary(returns:keySelector,returns:elementSelector)", "")]
    [InlineData("ToLookup", "ToLookup", "*:[E(source)]", "", "")]
    [InlineData("Where, SkipWhile, TakeWhile, DistinctBy, OrderBy, OrderByDescending, ThenBy, ThenByDescending",
                "Where SkipWhile TakeWhile DistinctBy OrderBy OrderByDescending ThenBy ThenByDescending", "*:[E(source)]", "sequence(E(source))", "")]
    [InlineData("ExceptBy, IntersectBy", "ExceptBy IntersectBy", "keySelector:[E(first)]", "sequence(E(first))", "reads-deep:second")]
    [InlineData("UnionBy", "UnionBy", "keySelector:[E(first),E(second)]", "sequence(E(first),E(second))", "")]
    [InlineData("Select", "Select", "selector:[E(source)]", "sequence(returns:selector)", "")]
    [InlineData("SelectMany(source, selector)", "SelectMany(source,selector)", "selector:[E(source)]", "sequence(elements(returns:selector))", "")]
    [InlineData("SelectMany(source, collectionSelector, resultSelector)", "SelectMany(source,collectionSelector,resultSelector)",
                "collectionSelector:[E(source)];resultSelector:[E(source)][elements(returns:collectionSelector)]", "sequence(returns:resultSelector)", "")]
    [InlineData("GroupBy(source, keySelector)", "GroupBy(source,keySelector)", "keySelector:[E(source)]",
                "sequence(grouping(returns:keySelector,E(source)))", "")]
    [InlineData("GroupBy(source, keySelector, elementSelector)", "GroupBy(source,keySelector,elementSelector)", "*:[E(source)]",
                "sequence(grouping(returns:keySelector,returns:elementSelector))", "")]
    [InlineData("GroupBy(…, resultSelector), without an element selector", "GroupBy(source,keySelector,resultSelector)",
                "keySelector:[E(source)];resultSelector:[returns:keySelector][sequence(E(source))]", "sequence(returns:resultSelector)", "")]
    [InlineData("GroupBy(…, resultSelector), with an element selector", "GroupBy(source,keySelector,elementSelector,resultSelector)",
                "keySelector:[E(source)];elementSelector:[E(source)];resultSelector:[returns:keySelector][sequence(returns:elementSelector)]",
                "sequence(returns:resultSelector)", "")]
    [InlineData("Join, LeftJoin, RightJoin", "Join LeftJoin RightJoin",
                "outerKeySelector:[E(outer)];innerKeySelector:[E(inner)];resultSelector:[E(outer)][E(inner)]", "sequence(returns:resultSelector)", "")]
    [InlineData("GroupJoin", "GroupJoin", "outerKeySelector:[E(outer)];innerKeySelector:[E(inner)];resultSelector:[E(outer)][sequence(E(inner))]",
                "sequence(returns:resultSelector)", "")]
    [InlineData("Zip(first, second, resultSelector)", "Zip(first,second,resultSelector)", "resultSelector:[E(first)][E(second)]",
                "sequence(returns:resultSelector)", "")]
    public void Operator_semantics(string row, string members, string inputs, string result, string effects)
    {
        Assert.NotEmpty(row);
        var specified = inputs.Split(';').Select(entry => entry.Split(':', 2))
                              .ToDictionary(pair => pair[0], pair => Regex.Matches(Expand(pair[1]), @"\[([^\[\]]*)\]").Select(match => match.Groups[1].Value).ToArray(),
                                            StringComparer.Ordinal);
        var overloads = members.Split(' ').SelectMany(Overloads).ToArray();

        Assert.NotEmpty(overloads);
        Assert.All(overloads, method =>
        {
            var member = Assert.Single(LibraryModels.BuiltIn.Members, candidate => candidate.Id == Id(method));
            var expectedResult = Expand(result);
            if (!method.Parameters.Any(parameter => parameter.Name == "defaultValue"))
                expectedResult = expectedResult.Replace(",arg:defaultValue", "", StringComparison.Ordinal);
            var kind = expectedResult.StartsWith("sequence(", StringComparison.Ordinal) ? "iterator" : "invoke-now";
            var expectedFates = method.Parameters.Where(parameter => parameter.Type.TypeKind == TypeKind.Delegate)
                                      .OrderBy(parameter => parameter.Name, StringComparer.Ordinal)
                                      .Select(parameter =>
                                      {
                                          var lists = specified.TryGetValue(parameter.Name, out var own) ? own : specified["*"];
                                          var invoke = ((INamedTypeSymbol)parameter.Type).DelegateInvokeMethod!.Parameters;
                                          if (invoke.Length == lists.Length + 1 && invoke[^1].Type.SpecialType == SpecialType.System_Int32)
                                              lists = [.. lists, ""];
                                          return $"{parameter.Name}:{kind}" + string.Concat(lists.Select(list => "[" + Sorted(list) + "]"));
                                      });

            Assert.Equal(expectedResult.Length == 0 ? null : expectedResult, member.Result?.ToString());
            Assert.Equal(string.Join(';', expectedFates), Fates(member));
            Assert.Equal(effects, Effects(member));
            Assert.Equal(LINQ_RANGE, Ranges(member));
        });
    }

    // ---- helpers ----

    private static IEnumerable<IMethodSymbol> Operators() =>
        Reference.Value.GetTypeByMetadataName("System.Linq.Enumerable")!.GetMembers().OfType<IMethodSymbol>()
                 .Where(method => method.DeclaredAccessibility == Accessibility.Public && method.MethodKind == MethodKind.Ordinary);

    /// <summary>The overloads one selector of <see cref="Operator_semantics"/> names, each taking a delegate and no comparer.</summary>
    private static IEnumerable<IMethodSymbol> Overloads(string selector)
    {
        var match = Regex.Match(selector, @"^(\w+)(?:``(\d))?(?:\(([\w,]+)\))?$");
        Assert.True(match.Success, selector);
        var overloads = Operators().Where(method => method.Name == match.Groups[1].Value && TakesADelegate(method) && !TakesAComparer(method))
                                    .Where(method => !match.Groups[2].Success || method.Arity == int.Parse(match.Groups[2].Value))
                                    .Where(method => !match.Groups[3].Success ||
                                                     string.Join(',', method.Parameters.Select(parameter => parameter.Name)) == match.Groups[3].Value)
                                    .ToArray();
        Assert.True(overloads.Length != 0, $"{selector} selects no overload");
        return overloads;
    }

    private static bool TakesADelegate(IMethodSymbol method) => method.Parameters.Any(parameter => parameter.Type.TypeKind == TypeKind.Delegate);

    private static bool TakesAComparer(IMethodSymbol method) =>
        method.Parameters.Any(parameter => parameter.Type.OriginalDefinition.Name is "IComparer" or "IEqualityComparer");

    private static IEnumerable<LibraryModel> Phase5a(string name) =>
        LibraryModels.BuiltIn.Members.Where(member => member.Id.StartsWith($"M:System.Linq.Enumerable.{name}``", StringComparison.Ordinal) &&
                                                      member.Fates.Count == 0 && member.Effects.Count != 0);

    private static IMethodSymbol Resolve(string id) =>
        Assert.IsAssignableFrom<IMethodSymbol>(Assert.Single(DocumentationCommentId.GetSymbolsForDeclarationId(id, Reference.Value)));

    private static string Id(IMethodSymbol method) => DocumentationCommentId.CreateDeclarationId(method)!;

    private static string Expand(string text) => Regex.Replace(text, @"E\((\w+)\)", "elements(arg:$1)");

    private static string Sorted(string list) =>
        string.Join(',', Regex.Split(list, @",(?![^(]*\))").Where(value => value.Length != 0).Order(StringComparer.Ordinal));

    private static string Fates(LibraryModel member) =>
        string.Join(';', member.Fates.OrderBy(fate => fate.Parameter, StringComparer.Ordinal).Select(fate =>
            $"{fate.Parameter}:{LibraryFate.Text(fate.Kind)}" +
            string.Concat((fate.Inputs ?? []).Select(input => "[" + string.Join(',', input.Select(value => value.ToString()).Order(StringComparer.Ordinal)) + "]"))));

    private static string Effects(LibraryModel member) =>
        string.Join(',', member.Effects.Select(effect => $"{Effect(effect.Kind)}:{effect.Parameter}")
                                       .Order(StringComparer.Ordinal));

    private static string Effect(LibraryEffectKind kind) => kind switch
    {
        LibraryEffectKind.DeepRead => "reads-deep",
        LibraryEffectKind.WriteArgument => "writes-arg",
        LibraryEffectKind.WriteCells => "writes-cells",
        _ => throw new System.Diagnostics.UnreachableException($"Unknown effect kind {kind}.")
    };

    private static string Ranges(LibraryModel member) =>
        string.Join(';', member.Assemblies.Select(range => $"{range.AssemblyName}@{range.Minimum}-{range.MaximumExclusive}"));
}
