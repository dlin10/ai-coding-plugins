using System.Collections;
using System.Reflection;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Ir;
using ConcurrencyHunter.Roots;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The content order a pair of one execution takes its first side from (ADR 0042) tells apart exactly what the fold tells
/// apart: two accesses compare equal by <see cref="PairContent.Compare(Access, Access)"/> exactly where
/// <see cref="AccessFolding.AccessKey"/> keeps them as one, and two variants by <see cref="PairContent.Compare(AccessVariant, AccessVariant)"/>
/// only where they hold the same content. Every stored field of an access with every list, map and record in it filled is changed one
/// at a time, by reflection over the records' fields, so that a field added to a record and left out of either order turns this red; the
/// fields the walk finds empty, which it cannot change, are named in the tests, so a field left empty is no silent gap.</summary>
public sealed class PairContentTests
{
    private static readonly SourceSpan SPAN = new("Case.cs", 3, 5, 3, 9);

    /// <summary>Every change of one stored field of a full access, with a selector of each kind that has fields: a key, two symbols,
    /// which show as the same text, and an exact index. The walk names the fields it found empty and could not change.</summary>
    [Fact]
    public void Two_accesses_compare_equal_exactly_where_the_fold_keeps_them_as_one_whichever_field_differs()
    {
        var wrong = new List<string>();
        var full = Full();
        // bounded: four selectors.
        foreach (var access in new[] { full, WithSelector(full, ElementSelector.Symbol("x")), WithSelector(full, ElementSelector.Symbol("y")),
                                       WithSelector(full, ElementSelector.Exact(3)) })
        {
            var unset = new List<string>();
            var changed = Changes(access, typeof(Access), "access", 0, unset).ToArray();
            // Two separately built equal accesses, every record, list and string of them an object of its own, are one access.
            var built = (Access)Copy(access)!;
            Assert.True(AccessFolding.AccessKey.INSTANCE.Equals(access, built));
            Assert.Equal(0, PairContent.Compare(access, built));
            Assert.Equal(0, PairContent.Compare(built, access));

            // bounded: one change of each stored field, each compared with the access and its copy both ways.
            foreach (var (path, value) in changed)
            {
                var other = (Access)value!;
                foreach (var one in new[] { access, built })
                {
                    var (order, reverse) = (PairContent.Compare(one, other), PairContent.Compare(other, one));
                    if (order == 0 != AccessFolding.AccessKey.INSTANCE.Equals(one, other) ||
                        order == 0 != AccessFolding.AccessKey.INSTANCE.Equals(other, one) || Math.Sign(order) != -Math.Sign(reverse))
                        wrong.Add($"{access.Resource.Selector} {path}: compare {order}/{reverse}, fold keeps them as one: " +
                                  AccessFolding.AccessKey.INSTANCE.Equals(one, other));
                }
            }

            // The access's executions are a variant's, not the fold's, and the one field left empty.
            Assert.Equal(["access.Executions"], unset);
            // The fields the review named are among those changed, and the fold tells them apart.
            string[] named =
            [
                "access.Conditions[0].SubjectTerm", "access.Conditions[0].Width", "access.Conditions[0].Signed",
                "access.Resource.Storage.Fields[0].IsVolatile", "access.Resource.Storage.Fields[0].IsContainingTypeReadOnly",
                "access.Resource.Storage.Fields[0].FieldTypeKey", "access.Resource.WildcardExclusions~", "access.Resource.Selector."
            ];
            Assert.All(named, name => Assert.Contains(changed, change => change.Path.StartsWith(name, StringComparison.Ordinal) &&
                                                                         !AccessFolding.AccessKey.INSTANCE.Equals(access, (Access)change.Value!)));
            // What a variant holds and the execution are not the fold's: those changes leave one access, by both.
            Assert.Contains(changed, change => change.Path.StartsWith("access.Root.", StringComparison.Ordinal) &&
                                               AccessFolding.AccessKey.INSTANCE.Equals(access, (Access)change.Value!));
            Assert.True(changed.Length > 150, $"{changed.Length} changes");
        }

        Assert.Empty(wrong);
        // Two symbols show as one text and are two cells by the fold and by content.
        var (x, y) = (WithSelector(full, ElementSelector.Symbol("x")), WithSelector(full, ElementSelector.Symbol("y")));
        Assert.Equal(x.Resource.Selector!.Text, y.Resource.Selector!.Text);
        Assert.False(AccessFolding.AccessKey.INSTANCE.Equals(x, y));
        Assert.NotEqual(0, PairContent.Compare(x, y));

        static Access WithSelector(Access access, ElementSelector selector) => access with { Resource = access.Resource with { Selector = selector } };
    }

    [Fact]
    public void Two_variants_compare_equal_only_where_they_hold_the_same_content_whichever_field_differs()
    {
        var access = Full();
        var variant = new AccessVariant(access.Root, access.PathRoot, access.CallPath, access.SpawnSites, access.CodeFlow, access.ReadSources);
        var unset = new List<string>();
        var changed = Changes(variant, typeof(AccessVariant), "variant", 0, unset).ToArray();
        var built = (AccessVariant)Copy(variant)!;
        Assert.Equal(0, PairContent.Compare(variant, built));
        // Every field of the variant is filled, so each can change.
        Assert.Empty(unset);

        // bounded: one change of each stored field, each compared with the variant and its copy both ways.
        var wrong = (from change in changed
                     from one in new[] { variant, built }
                     let order = PairContent.Compare(one, (AccessVariant)change.Value!)
                     let reverse = PairContent.Compare((AccessVariant)change.Value!, one)
                     where order == 0 || Math.Sign(order) != -Math.Sign(reverse)
                     select $"{change.Path}: compare {order}/{reverse}").ToArray();

        Assert.Empty(wrong);
        Assert.Contains(changed, change => change.Path.StartsWith("variant.PathRoot.Policy.", StringComparison.Ordinal));
        Assert.Contains(changed, change => change.Path.StartsWith("variant.Root.ReceiverTypeKey", StringComparison.Ordinal));
    }

    /// <summary>The content order reads first what a finding's group reads of the resource, the member by the text of its identity, and
    /// the site only after: of two accesses whose bodies order them one way, the scope, the assembly, the region, the path or the member
    /// orders them the other, and two members order by their identities' text, which reads a declaring type's identity before its name,
    /// not by their fields one by one (ADR 0042).</summary>
    [Fact]
    public void The_content_order_reads_what_a_group_reads_first_and_a_member_by_its_identity()
    {
        var full = Full();
        // The body puts the second first; the resource's field puts the first first.
        (Access, Access) Apart(Func<AccessResource, AccessResource> first, Func<AccessResource, AccessResource> second) =>
            (full with { Resource = first(full.Resource), BodyId = "body:z" }, full with { Resource = second(full.Resource), BodyId = "body:a" });

        (string Field, (Access, Access) Pair)[] cases =
        [
            ("scope", Apart(resource => resource with { Scope = "scope:a" }, resource => resource with { Scope = "scope:b" })),
            ("assembly", Apart(resource => resource with { Assembly = "A" }, resource => resource with { Assembly = "B" })),
            ("region", Apart(resource => resource with { RegionId = "region:a" }, resource => resource with { RegionId = "region:b" })),
            ("path", Apart(resource => resource with { AccessPath = ["a"] }, resource => resource with { AccessPath = ["b"] })),
            ("member's type", Apart(resource => resource with { Member = new MemberKey("A", "Value", IrFieldKind.Field) },
                                    resource => resource with { Member = new MemberKey("B", "Value", IrFieldKind.Field) })),
            // The identity reads the declaring type's identity, where the member's fields would read its display first.
            ("member's type identity", Apart(resource => resource with { Member = new MemberKey("Z", "Value", IrFieldKind.Field, "A@x") },
                                             resource => resource with { Member = new MemberKey("A", "Value", IrFieldKind.Field, "B@x") }))
        ];

        Assert.All(cases, item => Assert.True(PairContent.Compare(item.Pair.Item1, item.Pair.Item2) < 0 &&
                                              PairContent.Compare(item.Pair.Item2, item.Pair.Item1) > 0, item.Field));
    }

    /// <summary>Two writes of one site that differ only in the term a condition binds its subject to are two accesses one request makes,
    /// so pairing ranks them by content once it is done, and that comparison reaches a term, where it checks for cancellation. A
    /// cancellation that comes there ends pairing as cancelled, not as the failed sort that wraps what its comparison throws.</summary>
    [Fact]
    public void A_cancellation_while_pairing_ranks_kept_sides_by_content_ends_pairing_as_cancelled()
    {
        var execution = EngineFixture.Execute("""
            public static class State { public static int Value; }
            public class VisitsController : ControllerBase { public void Post() => State.Value = 1; }
            """ + EngineFixture.Startup());
        var write = Assert.Single(EngineFixture.Collect(execution).Accesses, access => access.Resource.Member.Name == "Value");
        Access Bound(string variable) =>
            write with { Conditions = [new PathPredicate(string.Empty, PathRelation.Unsupported, null, "?") { SubjectTerm = new VariableTerm(variable, 32) }] };
        var folding = new AccessFolding();
        folding.Add([Bound("a"), Bound("b")], execution.Analysis.Executions.Select(made => made.Id).ToList().IndexOf(write.ExecutionId));
        var accesses = folding.Accesses();
        Assert.Equal(2, accesses.Count);
        // Pairing alone finds two kept sides of one execution, which it ranks by content.
        Assert.Equal(3, InterproceduralPairing.Pair(accesses, execution.Analysis, execution.Heap.Heap).Pairs.Count);

        using var cancellation = new CancellationTokenSource();
        var ranking = false;
        using (WorkCancellation.Enter(cancellation.Token, () =>
               {
                   if (!Environment.StackTrace.Contains("KeptViews.Places", StringComparison.Ordinal))
                       return;
                   ranking = true;
                   cancellation.Cancel();
               }))
        {
            Assert.Throws<OperationCanceledException>(() => InterproceduralPairing.Pair(accesses, execution.Analysis, execution.Heap.Heap));
        }

        Assert.True(ranking);
    }

    /// <summary>An access with every field set and every list, map and record in it filled, so that each of their fields can change.</summary>
    private static Access Full()
    {
        var member = new MemberKey("State", "Value", IrFieldKind.Field, "State@Fixture");
        var field = new IrFieldRef("Fixture", "State", "Value", IrFieldKind.Field, true, false, "int", "State@Fixture")
        {
            IsVolatile = true, IsContainingTypeReadOnly = true, FieldTypeKey = "int@System"
        };
        var resource = new AccessResource("Fixture", "scope:Fixture", "static:", ["State", "Value"], member, "region:1", true)
        {
            WildcardExclusions = new HashSet<string>(StringComparer.Ordinal) { "excluded" },
            StructPath = [member],
            Storage = new StoragePath([field, field], 1, "region:1", "slot"),
            Selector = ElementSelector.Key("key"),
            CollectionId = "collection:1",
            RegionKey = "key:1"
        };
        var root = new AccessRoot("root:1", "Root()", "Root", "provider", "request", new InvocationPolicy(Multiplicity.Repeated, SelfOverlap.MayOverlap, "scope"),
                                  "scope:Fixture", "Receiver", "Receiver@Fixture");
        var step = new CodeFlowStep("call", "Bump()", SPAN);
        var check = new GapCheck("L.Gap()", "opaque", "timing", "because");
        return new Access(resource, AccessOperation.ReadModifyWrite, root, "State.Value", SPAN, ["lock G"], ["object:G"], [new BindingEvidence("kind", "text", SPAN)],
                          [step], ["uncertain"])
        {
            UnseenWriteOperation = AccessOperation.Write,
            WritesWholeStruct = true,
            ExecutionId = "execution:1",
            Ownership = OwnershipKind.Escaped,
            OwnershipEvidence = ["evidence"],
            IsConstructionLocal = true,
            IsReferenceAccess = true,
            ReadSources = [new ReadSource("source", SPAN, [step])],
            BodyId = "body:Fixture:M:State.Bump",
            OperationId = 4,
            InstanceId = "instance:1",
            CallPath = ["Root()", "Bump()"],
            SpawnSites = [new SpawnSiteLocation("spawn:1", SPAN)],
            PathRoot = root with { RootId = "root:2" },
            HeldProtections = new Dictionary<string, IReadOnlyList<HeldProtection>>(StringComparer.Ordinal)
            {
                ["object:G"] = [new HeldProtection(IrSynchronizationPrimitive.Lock, IrLockMode.Read, true)]
            },
            Conditions = [new PathPredicate("canon|subject", PathRelation.Less, "3", "subject < 3")
                          {
                              SubjectTerm = new ConvertTerm(new SumTerm(new VariableTerm("v", 32), new ConstantTerm(1, 32)), 64, false),
                              Width = 64,
                              Signed = false
                          }],
            IsIterationIndexed = true,
            IsFresh = true,
            IsLibraryInternal = true,
            SelectorTerm = new VariableTerm("index", 32),
            GapChecks = [check],
            DecidingCalls = [new DecidingCall(check, new CallIdentity("body:Fixture:M:State.Bump", SPAN, "L.Gap()", 1))]
        };
    }

    /// <summary>Every way to change one stored field of a value, each with the path of the field changed: a string, number, flag or enum
    /// to another value, a field that may be null to null, a list longer or with its first item changed, a set with another item or one
    /// item replaced, a map with another entry or a changed one, and a record or other object of the analysis one of its own fields at a
    /// time. A null or an empty list has nothing to change inside it, and its path goes to <paramref name="unset"/>.</summary>
    /// <param name="value">The value.</param>
    /// <param name="type">The type of the field that holds it.</param>
    /// <param name="path">Its path from the access.</param>
    /// <param name="depth">How deep the path is.</param>
    /// <param name="unset">Gets the paths of the nulls and empty lists met.</param>
    private static IEnumerable<(string Path, object? Value)> Changes(object? value, Type type, string path, int depth, List<string> unset)
    {
        Assert.True(depth < 12, path);
        switch (value)
        {
            case null:
                unset.Add(path);
                yield break;
            case string text:
                yield return (path, text + "~");
                yield break;
            case bool flag:
                yield return (path, !flag);
                yield break;
            case int number:
                yield return (path, number + 1);
                yield break;
            case long number:
                yield return (path, number + 1);
                yield break;
            case Enum kind:
                var kinds = Enum.GetValues(kind.GetType()).Cast<object>().ToArray();
                yield return (path, kinds[(Array.IndexOf(kinds, kind) + 1) % kinds.Length]);
                yield break;
            case IReadOnlyDictionary<string, IReadOnlyList<HeldProtection>> held:
                var (id, protections) = held.First();
                yield return ($"{path}+", new Dictionary<string, IReadOnlyList<HeldProtection>>(held, StringComparer.Ordinal) { ["object:H"] = protections });
                yield return ($"{path}[{id}]~", new Dictionary<string, IReadOnlyList<HeldProtection>>(StringComparer.Ordinal) { [id + "~"] = protections });
                // bounded: the changes of the first entry's protections.
                foreach (var (inner, changed) in Changes(protections, typeof(IReadOnlyList<HeldProtection>), $"{path}[{id}]", depth + 1, unset))
                    yield return (inner, new Dictionary<string, IReadOnlyList<HeldProtection>>(held, StringComparer.Ordinal) { [id] = (IReadOnlyList<HeldProtection>)changed! });
                yield break;
            case IReadOnlySet<string> set:
                yield return ($"{path}+", new HashSet<string>(set.Append("other"), StringComparer.Ordinal));
                // As many items, one of them another.
                yield return ($"{path}~", new HashSet<string>(set.Skip(1).Append(set.First() + "~"), StringComparer.Ordinal));
                yield break;
            case IEnumerable items when Item(type) is { } item:
                var list = items.Cast<object?>().ToList();
                if (list.Count == 0)
                {
                    unset.Add(path);
                    yield break;
                }
                yield return ($"{path}+", List(item, list.Append(list[0])));
                // bounded: the changes of the first item.
                foreach (var (inner, changed) in Changes(list[0], item, $"{path}[0]", depth + 1, unset))
                    yield return (inner, List(item, list.Skip(1).Prepend(changed)));
                yield break;
        }

        // bounded: the stored fields of one record, each changed in turn.
        foreach (var field in Fields(value.GetType()))
        {
            var name = $"{path}.{Name(field)}";
            var held = field.GetValue(value);
            if (held is not null && MayBeNull(field))
                yield return ($"{name}=null", With(value, field, null));
            // bounded: the changes of one field.
            foreach (var (inner, changed) in Changes(held, field.FieldType, name, depth + 1, unset))
                yield return (inner, With(value, field, changed));
        }
    }

    /// <summary>A copy of a value that shares no record, list, map or string with it.</summary>
    /// <param name="value">The value.</param>
    private static object? Copy(object? value)
    {
        switch (value)
        {
            case null or bool or int or long or Enum:
                return value;
            case string text:
                return new string(text.AsSpan());
            case IReadOnlyDictionary<string, IReadOnlyList<HeldProtection>> held:
                return held.ToDictionary(entry => (string)Copy(entry.Key)!, entry => (IReadOnlyList<HeldProtection>)Copy(entry.Value)!, StringComparer.Ordinal);
            case IReadOnlySet<string> set:
                return new HashSet<string>(set.Select(text => (string)Copy(text)!), StringComparer.Ordinal);
            case IEnumerable items when Item(value.GetType()) is { } item:
                return List(item, items.Cast<object?>().Select(Copy));
        }

        var copy = Clone(value);
        // bounded: the stored fields of one record.
        foreach (var field in Fields(value.GetType()))
            field.SetValue(copy, Copy(field.GetValue(value)));
        return copy;
    }

    /// <summary>The fields a record stores, its base records' among them.</summary>
    /// <param name="type">The record's type.</param>
    private static IEnumerable<FieldInfo> Fields(Type type)
    {
        // bounded: the record and its base types.
        for (var at = type; at is not null && at != typeof(object); at = at.BaseType)
        {
            // bounded: one type's fields.
            foreach (var field in at.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return field;
        }
    }

    /// <summary>The property a backing field stores, or the field's own name.</summary>
    /// <param name="field">The field.</param>
    private static string Name(FieldInfo field) =>
        field.Name.StartsWith('<') ? field.Name[1..field.Name.IndexOf('>', StringComparison.Ordinal)] : field.Name;

    /// <summary>Whether a field may hold null: a nullable value type, or a reference its declaration lets be null.</summary>
    /// <param name="field">The field.</param>
    private static bool MayBeNull(FieldInfo field) =>
        Nullable.GetUnderlyingType(field.FieldType) is not null ||
        !field.FieldType.IsValueType && new NullabilityInfoContext().Create(field).WriteState == NullabilityState.Nullable;

    /// <summary>A copy of a record with one field set anew.</summary>
    /// <param name="value">The record, a boxed one for a struct.</param>
    /// <param name="field">The field.</param>
    /// <param name="changed">Its new value.</param>
    private static object With(object value, FieldInfo field, object? changed)
    {
        var copy = Clone(value);
        field.SetValue(copy, changed);
        return copy;
    }

    private static object Clone(object value) =>
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, null)!;

    /// <summary>The item type of a list type, or null for none.</summary>
    /// <param name="type">The list's type.</param>
    private static Type? Item(Type type) =>
        (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? type : null)?.GetGenericArguments()[0] ??
        type.GetInterfaces().FirstOrDefault(face => face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))?.GetGenericArguments()[0];

    /// <summary>A list of an item type holding items.</summary>
    /// <param name="item">The item type.</param>
    /// <param name="items">The items.</param>
    private static IList List(Type item, IEnumerable<object?> items)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(item))!;
        // bounded: the items of one list.
        foreach (var each in items)
            list.Add(each);
        return list;
    }
}
