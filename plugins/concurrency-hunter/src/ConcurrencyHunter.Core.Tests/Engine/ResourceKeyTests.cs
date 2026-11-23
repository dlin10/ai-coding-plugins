using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Ir;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A resource's identity is the numbers of its parts (<see cref="AccessResource.KeyIn"/>, issue 474): two resources have one key
/// exactly where they have one identity text, the text joined on a read is the one the resource kept before, and the order that reads
/// the text without joining it is the ordinal order of the text. Checked on every resource of the demo, on a fixture whose resources are
/// cells of collections, struct fields below them and a collection two fields hold, and on resources whose parts are prefixes of each
/// other, where an order by parts and the order of the text part ways.</summary>
public sealed class ResourceKeyTests
{
    private const string SCOPE = "scope:Fixture";

    [Fact]
    public async Task On_the_demo_two_resources_have_one_key_exactly_where_they_have_one_identity()
    {
        var demo = await DemoExpectationTests.SharedDemo.Value;

        var resources = demo.Folded.Select(access => access.Resource)
                            .Concat(demo.Findings.SelectMany(finding => new[] { finding.Resource, finding.AccessA.Resource, finding.AccessB.Resource }))
                            .ToArray();

        Check(resources);
        Assert.Contains(resources, resource => resource.Selector is not null);
    }

    [Fact]
    public void On_cells_struct_fields_and_a_collection_two_fields_hold_two_resources_have_one_key_exactly_where_they_have_one_identity()
    {
        var run = AnalyzeScope(FixtureSolution.Create(("Case.cs", Source())), SCOPE);

        var resources = run.Collection.Accesses.Select(access => access.Resource)
                           .Concat(run.Pairs.Pairs.Select(pair => pair.Resource))
                           .ToArray();

        Check(resources);
        Assert.Contains(resources, resource => resource is { Selector: not null, StructPath.Count: > 0 });
        Assert.Contains(resources, resource => resource is { Selector: null, StructPath.Count: > 1 });
        Assert.Contains(resources, resource => resource is { CollectionId: not null, Selector: not null });
        Assert.Contains(resources, resource => resource is { CollectionId: not null, Selector: null });
        // The list two fields hold is one collection, whichever field names it.
        var list = resources.Where(resource => resource is { CollectionId: not null, Selector: null } &&
                                               resource.AccessPath is ["_list"] or ["_alias"])
                            .ToArray();
        Assert.Equal(["_alias", "_list"], list.Select(resource => resource.AccessPath[0]).Distinct().Order(StringComparer.Ordinal));
        Assert.Single(list.Select(resource => resource.Identity).Distinct());
    }

    /// <summary>Where one part is a prefix of the same part of another resource, the text's next character after the shorter part is the
    /// separator that follows it, which an order by parts would not read: a region, a member's name, a struct field below and a cell;
    /// with them a collection's resources and the region's wildcard.</summary>
    [Fact]
    public void Resources_whose_parts_are_prefixes_of_each_other_are_in_the_order_of_their_texts()
    {
        var member = new MemberKey("Fixture:Holder", "X", IrFieldKind.Field);
        var field = new AccessResource("Fixture", SCOPE, "static:Holder", ["X"], member, "static:Fixture:Holder");
        var resources = new[]
        {
            field,
            field with { RegionId = "static:Fixture:Holder2" },
            field with { RegionId = "static:Fixture:Holder|2" },
            field with { Member = member with { Name = "X2" } },
            field with { Member = member with { Name = "X", DeclaringTypeIdentity = "Fixture:Holder`1" } },
            field with { StructPath = [new MemberKey("Fixture:Inner", "Y", IrFieldKind.Field)] },
            field with { StructPath = [new MemberKey("Fixture:Inner", "Y", IrFieldKind.PropertyBackingField)] },
            field with { Selector = ElementSelector.Exact(3) },
            field with { Selector = ElementSelector.Exact(3), StructPath = [new MemberKey("Fixture:Inner", "Y", IrFieldKind.Field)] },
            field with { CollectionId = "alloc|Fixture:Holder..ctor#1" },
            field with { CollectionId = "alloc|Fixture:Holder..ctor#1", Selector = ElementSelector.Exact(1) },
            field with { CollectionId = "alloc|Fixture:Holder..ctor#10" },
            field with { Scope = "scope:Fixture2" },
            field with { AccessPath = ["*"], Member = new MemberKey("*", "*", IrFieldKind.Field), IsWildcard = true }
        };

        Check(resources);
        // The region "...Holder2" comes before "...Holder" followed by the bar, as the texts read; the parts alone would say otherwise.
        Assert.True(AccessResource.IDENTITY_ORDER.Compare(resources[1], resources[0]) < 0);
        Assert.True(string.CompareOrdinal(resources[0].RegionId, resources[1].RegionId) < 0);
    }

    /// <summary>Checks that the key, the identity text and the text order agree on every resource given: each text has one key and each
    /// key one text, for the identity and for the structural identity; the text is the one the resource joined before it stopped
    /// keeping it; <see cref="AccessResource.SameIdentity"/> says what the texts say; and the resources sort alike by the order that
    /// reads the text without joining it and by the ordinal order of the text.</summary>
    /// <param name="resources">The resources.</param>
    private static void Check(IReadOnlyList<AccessResource> resources)
    {
        Assert.NotEmpty(resources);
        var numbers = new TextNumbers();
        var keys = new Dictionary<string, ResourceKey>(StringComparer.Ordinal);
        var texts = new Dictionary<ResourceKey, string>();
        var structuralKeys = new Dictionary<string, ResourceKey>(StringComparer.Ordinal);
        var structuralTexts = new Dictionary<ResourceKey, string>();
        foreach (var resource in resources)
        {
            var (identity, structural) = (resource.Identity, resource.StructuralIdentity);
            Assert.Equal(Joined(resource), identity);
            Assert.Equal(JoinedStructural(resource), structural);
            var key = resource.KeyIn(numbers);
            Assert.Equal(key, resource.KeyIn(numbers));
            OneToOne(keys, texts, identity, key);
            OneToOne(structuralKeys, structuralTexts, structural, key.Structural);
        }

        var byText = resources.OrderBy(resource => resource.Identity, StringComparer.Ordinal).ToArray();
        var byOrder = resources.OrderBy(resource => resource, AccessResource.IDENTITY_ORDER).ToArray();
        Assert.Equal(byText.Select(resource => resource.Identity), byOrder.Select(resource => resource.Identity));
        for (var at = 0; at < byText.Length; at++)
        {
            Assert.Same(byText[at], byOrder[at]);
            if (at == 0)
                continue;
            var (before, after) = (byText[at - 1], byText[at]);
            var same = string.Equals(before.Identity, after.Identity, StringComparison.Ordinal);
            Assert.Equal(same, AccessResource.SameIdentity(before, after));
            Assert.Equal(Math.Sign(string.CompareOrdinal(before.Identity, after.Identity)), Math.Sign(AccessResource.IDENTITY_ORDER.Compare(before, after)));
            Assert.Equal(Math.Sign(string.CompareOrdinal(after.Identity, before.Identity)), Math.Sign(AccessResource.IDENTITY_ORDER.Compare(after, before)));
        }
    }

    /// <summary>Records a text with its key and fails where the text already had another key or the key another text.</summary>
    /// <param name="keys">The key of each text met.</param>
    /// <param name="texts">The text of each key met.</param>
    /// <param name="text">The text.</param>
    /// <param name="key">Its key.</param>
    private static void OneToOne(Dictionary<string, ResourceKey> keys, Dictionary<ResourceKey, string> texts, string text, ResourceKey key)
    {
        if (!keys.TryAdd(text, key))
            Assert.Equal(keys[text], key);
        if (!texts.TryAdd(key, text))
            Assert.Equal(texts[key], text);
    }

    /// <summary>The identity as the resource joined and kept it before it stopped keeping the text.</summary>
    /// <param name="resource">The resource.</param>
    private static string Joined(AccessResource resource) =>
        resource.Selector is null ? JoinedStructural(resource) : $"{JoinedStructural(resource)}|{resource.Selector.Text}";

    /// <summary>The structural identity as the resource joined and kept it before it stopped keeping the text.</summary>
    /// <param name="resource">The resource.</param>
    private static string JoinedStructural(AccessResource resource) =>
        (resource.CollectionId is { } collection
            ? $"{resource.Scope}|{collection}"
            : $"{resource.Scope}|{resource.Assembly}|{resource.RegionId ?? resource.Region}|{resource.Member.Identity}") +
        string.Concat(resource.StructPath.Select(member => $"/{member.Identity}"));

    /// <summary>A singleton holder whose fields are cells of an array and of a dictionary, struct fields below a field and below a cell, a
    /// struct past the depth bound, a list two fields hold, a dictionary and a static counter, which two workers touch.</summary>
    private static string Source() => Usings + """
        using System.Collections.Generic;

        public struct Slot { public int Hits; }
        public struct Inner { public int Count; }
        public struct Outer { public Inner Inner; }
        public struct Deepest { public int Count; }
        public struct Deeper { public Deepest Deepest; }
        public struct Deep { public Deeper Deeper; }
        public struct Deepest4 { public Deep Deep; }
        public struct Cell { public int X; }

        public struct Rack
        {
            public Cell[] Items;
            public static Rack One() { var rack = new Rack(); rack.Items = new Cell[4]; return rack; }
        }

        public sealed class Holder
        {
            public static int s_count;
            private readonly Slot[] _slots = new Slot[4];
            private readonly Dictionary<string, int> _map = new();
            private readonly List<int> _list = new();
            private readonly List<int> _alias;
            private Outer _outer;
            private Deepest4 _deepest;
            private Rack _rack = Rack.One();

            public Holder() { _alias = _list; }

            public void Hit(int i)
            {
                _slots[0].Hits++;
                _slots[i].Hits++;
                _map["a"] = i;
                _map[i.ToString()] = i;
                _list.Add(i);
                _alias.Clear();
                _outer.Inner.Count++;
                _outer = default;
                _deepest.Deep.Deeper.Deepest.Count++;
                _rack.Items[0].X++;
                s_count++;
            }
        }

        public sealed class FirstWorker : BackgroundService
        {
            private readonly Holder _holder;
            public FirstWorker(Holder holder) { _holder = holder; }
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                _holder.Hit(1);
                return Task.CompletedTask;
            }
        }

        public sealed class SecondWorker : BackgroundService
        {
            private readonly Holder _holder;
            public SecondWorker(Holder holder) { _holder = holder; }
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                _holder.Hit(2);
                return Task.CompletedTask;
            }
        }

        """ + Startup("services.AddSingleton<Holder>(); services.AddHostedService<FirstWorker>(); services.AddHostedService<SecondWorker>();");
}
