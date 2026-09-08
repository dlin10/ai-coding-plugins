using CacheDetective.Caching;
using CacheDetective.Events;
using CacheDetective.Graph;
using CacheDetective.Indexing;
using CacheDetective.Tests.Fixtures;
using Xunit;

namespace CacheDetective.Tests.Caching;

/// <summary>A key that arrives as an object rather than a string, folded through the recognizer's
/// key-object description (<c>docs/adr/0016</c>). The fixture is shaped like nopCommerce, which is the
/// corpus these rows come from: a key type carrying a template, a defaults class of static properties,
/// and a key service whose factories substitute into the template's positional holes.</summary>
public sealed class KeyObjectFoldingTests
{
    private const string CACHE = "KeyObjectFixture.IObjectCache";
    private const string KEY_SERVICE = "KeyObjectFixture.IObjectKeyService";
    private const string KEY_OBJECT = "KeyObjectFixture.ObjectCacheKey";

    [Fact]
    public async Task A_construction_at_the_call_site_folds()
    {
        var templates = await TemplatesAsync();

        Assert.Contains("fixture.inline", templates);
    }

    [Fact]
    public async Task A_defaults_class_property_is_followed_to_its_construction()
    {
        var templates = await TemplatesOfAsync("FromDefaults");

        Assert.Equal(["fixture.direct"], templates);
    }

    /// <summary>The whole point: the holes take the names of the argument expressions, exactly as an
    /// interpolation hole does.</summary>
    [Fact]
    public async Task A_factory_substitutes_its_arguments_into_the_holes()
    {
        var templates = await TemplatesAsync();

        Assert.Contains("fixture.category.all.{storeId}-{roleIds}-{showHidden}", templates);
    }

    /// <summary>nopCommerce writes its templates as <c>{{0}}</c> inside an interpolated string. The
    /// escaped brace is the literal hole, so a substituted template carries no doubled brace at all.
    /// </summary>
    [Fact]
    public async Task An_escaped_brace_template_resolves_to_the_literal_hole()
    {
        var templates = await TemplatesAsync();

        // Both call sites over the escaped template substituted into it, and no doubled brace survived
        // anywhere: the escape was the hole, not two characters around a number.
        var escaped = templates.Where(template => template.StartsWith("fixture.category.all.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, escaped.Length);
        Assert.All(escaped, template => Assert.DoesNotContain("{{", template, StringComparison.Ordinal));
        Assert.All(escaped, template => Assert.DoesNotContain("}}", template, StringComparison.Ordinal));
        Assert.All(escaped, template => Assert.Contains("{storeId}", template, StringComparison.Ordinal));
    }

    /// <summary>
    /// The same escaped hole in an interpolation that is <em>not</em> a compile-time constant, which is a
    /// different path entirely: a constant interpolation is decoded by the compiler and never reaches the
    /// interpolation walk, so every other escaped-brace case in this fixture proves nothing about this one.
    /// This is nopCommerce's <c>NopEntityCacheDefaults&lt;TEntity&gt;</c>, a whole family of its keys, and
    /// the hole has to be decoded here or the factory substitutes into nothing and silently drops its
    /// argument.
    /// </summary>
    [Fact]
    public async Task An_escaped_hole_in_a_non_constant_interpolation_takes_its_argument()
    {
        var templates = await TemplatesAsync();

        Assert.Contains("fixture.{EntityTypeName}.byid.{entityId}", templates);
    }

    /// <summary>And no doubled brace survives anywhere, on either path.</summary>
    [Fact]
    public async Task No_template_keeps_a_doubled_brace()
    {
        var templates = await TemplatesAsync();

        Assert.All(templates, template => Assert.DoesNotContain("{{", template, StringComparison.Ordinal));
        Assert.All(templates, template => Assert.DoesNotContain("}}", template, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_hole_with_no_matching_argument_is_unknown()
    {
        var templates = await TemplatesAsync();

        Assert.Contains("fixture.category.all.{storeId}-{?}-{?}", templates);
    }

    [Fact]
    public async Task An_argument_with_no_matching_hole_is_dropped()
    {
        var templates = await TemplatesOfAsync("MoreArguments");

        Assert.Equal(["fixture.pair.{left}-{right}"], templates);
        Assert.DoesNotContain(templates, template => template.Contains("surplus", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_argument_that_names_nothing_is_the_unknown_hole()
    {
        var templates = await TemplatesAsync();

        Assert.Contains("fixture.pair.{left}-{?}", templates);
    }

    [Fact]
    public async Task A_key_object_reached_through_a_local_folds()
    {
        var templates = await TemplatesAsync();

        Assert.Contains("fixture.pair.{first}-{second}", templates);
    }

    /// <summary>The factory's result assigned to a local and the local passed on — nopCommerce's
    /// <c>CategoryService</c> shape. Recognising the factory only at the outermost expression left the site
    /// unresolved with both its template and its factory sitting in the compilation.</summary>
    [Fact]
    public async Task A_factory_call_reached_through_a_local_folds()
    {
        var templates = await TemplatesOfAsync("FactoryThroughLocal");

        Assert.Equal(["fixture.pair.{left}-{right}"], templates);
    }

    /// <summary>A local holding a factory result in one branch and a construction in the other names both,
    /// so the two shapes compose exactly as two constructions do.</summary>
    [Fact]
    public async Task A_local_holding_a_factory_result_or_a_construction_names_both()
    {
        var templates = await TemplatesOfAsync("FactoryOrConstructionLocal");

        Assert.Equal(2, templates.Length);
        Assert.Contains("fixture.direct", templates);
        Assert.Contains("fixture.pair.{left}-{right}", templates);
    }

    /// <summary>A key object reached through a local assigned in two places names both templates, exactly as
    /// a branchy string local does (<c>docs/adr/0015</c>). Naming only the initialiser would make a site that
    /// has a choice look certain, and a certain removal suppresses.</summary>
    [Fact]
    public async Task A_key_object_local_assigned_in_two_branches_names_both()
    {
        var templates = await TemplatesOfAsync("BranchyLocal");

        Assert.Equal(2, templates.Length);
        Assert.Contains("fixture.direct", templates);
        Assert.Contains("fixture.branch", templates);
    }

    /// <summary>The same branch written as one expression. It used to be followed to no construction at all,
    /// so the site was wholly unresolved rather than a set of two.</summary>
    [Fact]
    public async Task An_object_valued_conditional_names_both_arms()
    {
        var templates = await TemplatesAsync();

        Assert.Contains("fixture.when-true", templates);
        Assert.Contains("fixture.when-false", templates);
    }

    /// <summary>A key object whose template is not a literal is no more knowable than a dynamic string
    /// key, and is recorded the same way.</summary>
    [Fact]
    public async Task A_template_that_is_not_a_literal_stays_unresolved()
    {
        var graph = await IndexAsync(WithKeyObject());

        Assert.DoesNotContain(graph.CacheKeys, key => key.Template.Contains("Guid", StringComparison.Ordinal));
        Assert.Contains(graph.Unresolved, item => item.Kind == UnresolvedKind.Key &&
                                                  item.Snippet.Contains("RuntimeKey", StringComparison.Ordinal));
    }

    /// <summary>A key object no recognizer describes is left exactly where it was: unresolved.</summary>
    [Fact]
    public async Task A_key_object_the_recognizer_does_not_describe_stays_unresolved()
    {
        var graph = await IndexAsync(WithKeyObject());

        Assert.DoesNotContain(graph.CacheKeys, key => key.Template == "fixture.undeclared");
    }

    /// <summary>Without the description nothing changes: every key-object site is an unresolved key, which
    /// is what nopCommerce measured before this task.</summary>
    [Fact]
    public async Task A_recognizer_without_a_key_object_folds_nothing()
    {
        var graph = await IndexAsync(WithoutKeyObject());

        Assert.DoesNotContain(graph.CacheKeys, key => key.Store == "memory");
        Assert.True(graph.Unresolved.Count(item => item.Kind == UnresolvedKind.Key) >= 8,
                    string.Join(Environment.NewLine, graph.Unresolved.Select(item => $"{item.Kind}: {item.Snippet}")));
    }

    /// <summary>The declaration adds keys and takes none away: every site that folded without it still
    /// folds, and the sites that did not are the ones the description reaches.</summary>
    [Fact]
    public async Task The_description_only_turns_unresolved_keys_into_templates()
    {
        var without = await IndexAsync(WithoutKeyObject());
        var with = await IndexAsync(WithKeyObject());

        Assert.True(with.CacheKeys.Count > without.CacheKeys.Count);
        Assert.True(with.Unresolved.Count(item => item.Kind == UnresolvedKind.Key) <
                    without.Unresolved.Count(item => item.Kind == UnresolvedKind.Key));
    }

    /// <summary>Every template the fixture keys on, whichever method produced it. Only for assertions about
    /// the graph as a whole; an assertion about one site wants <see cref="TemplatesOfAsync"/>.</summary>
    private static async Task<string[]> TemplatesAsync() =>
        (await IndexAsync(WithKeyObject())).CacheKeys.Where(key => key.Store == "memory")
                                           .Select(key => key.Template)
                                           .ToArray();

    /// <summary>The templates one fixture method keys on. Several methods here fold to the same template —
    /// three produce <c>fixture.pair.{left}-{right}</c> and three produce <c>fixture.direct</c> — so a test
    /// that searched the whole graph would go on passing after its own subject stopped folding, which is no
    /// test at all.</summary>
    private static async Task<string[]> TemplatesOfAsync(string method) =>
        [.. (await IndexAsync(WithKeyObject())).Edges.OfType<Caches>()
            .Where(edge => ((Handler)edge.From).Symbol.Contains(method, StringComparison.Ordinal))
            .Select(edge => ((CacheKey)edge.To).Template)];

    private static async Task<CacheGraph> IndexAsync(CacheRecognizer recognizer)
    {
        var solution = await FixtureSolution.CreateAsync("SourceFiles/KeyObjects.cs");
        return await new CallGraphIndexer(new IndexerOptions([recognizer, .. CacheRecognizers.All], EventRecognizers.All))
            .IndexAsync(solution, "fixture");
    }

    private static CacheRecognizer WithoutKeyObject() =>
        new(CACHE, "memory", [new CacheMethodRecognizer("Set", CacheSemantic.Set, 0)]);

    private static CacheRecognizer WithKeyObject() =>
        WithoutKeyObject() with
        {
            KeyObject = new KeyObjectRecognizer(KEY_OBJECT, 0,
                [new KeyObjectFactory(KEY_SERVICE, ["PrepareKey", "PrepareKeyForDefaultCache"], 0, 1)])
        };
}
