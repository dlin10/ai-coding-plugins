using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Frontend;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>Every member of every interface a type of the table or an array implements is decided on an object of that type (R4, R5), or
/// is on the list of members that stay what a direct call of them is, opaque with their unknown effect. The list is kept here by hand, so
/// a member the map decides differently, and one a new version of the framework adds, is a decision someone makes.</summary>
public sealed class InterfaceMemberCoverageTests
{
    /// <summary>The members of the interfaces of each kind that no member of the table models, as kind, interface and member.</summary>
    private static readonly string[] Opaque =
    [
        "System.Collections.Concurrent.ConcurrentBag: System.Collections.Concurrent.IProducerConsumerCollection<T>.CopyTo",
        "System.Collections.Concurrent.ConcurrentBag: System.Collections.Concurrent.IProducerConsumerCollection<T>.ToArray",
        "System.Collections.Concurrent.ConcurrentBag: System.Collections.ICollection.CopyTo",
        "System.Collections.Concurrent.ConcurrentBag: System.Collections.ICollection.get_IsSynchronized",
        "System.Collections.Concurrent.ConcurrentBag: System.Collections.ICollection.get_SyncRoot",
        "System.Collections.Concurrent.ConcurrentDictionary: System.Collections.Generic.ICollection<T>.Contains",
        "System.Collections.Concurrent.ConcurrentDictionary: System.Collections.Generic.ICollection<T>.CopyTo",
        "System.Collections.Concurrent.ConcurrentDictionary: System.Collections.Generic.ICollection<T>.get_IsReadOnly",
        "System.Collections.Concurrent.ConcurrentDictionary: System.Collections.ICollection.CopyTo",
        "System.Collections.Concurrent.ConcurrentDictionary: System.Collections.ICollection.get_IsSynchronized",
        "System.Collections.Concurrent.ConcurrentDictionary: System.Collections.ICollection.get_SyncRoot",
        "System.Collections.Concurrent.ConcurrentDictionary: System.Collections.IDictionary.get_IsFixedSize",
        "System.Collections.Concurrent.ConcurrentDictionary: System.Collections.IDictionary.get_IsReadOnly",
        "System.Collections.Concurrent.ConcurrentQueue: System.Collections.Concurrent.IProducerConsumerCollection<T>.CopyTo",
        "System.Collections.Concurrent.ConcurrentQueue: System.Collections.Concurrent.IProducerConsumerCollection<T>.ToArray",
        "System.Collections.Concurrent.ConcurrentQueue: System.Collections.ICollection.CopyTo",
        "System.Collections.Concurrent.ConcurrentQueue: System.Collections.ICollection.get_IsSynchronized",
        "System.Collections.Concurrent.ConcurrentQueue: System.Collections.ICollection.get_SyncRoot",
        "System.Collections.Concurrent.ConcurrentStack: System.Collections.Concurrent.IProducerConsumerCollection<T>.CopyTo",
        "System.Collections.Concurrent.ConcurrentStack: System.Collections.Concurrent.IProducerConsumerCollection<T>.ToArray",
        "System.Collections.Concurrent.ConcurrentStack: System.Collections.ICollection.CopyTo",
        "System.Collections.Concurrent.ConcurrentStack: System.Collections.ICollection.get_IsSynchronized",
        "System.Collections.Concurrent.ConcurrentStack: System.Collections.ICollection.get_SyncRoot",
        "System.Collections.Generic.Dictionary.KeyCollection: System.Collections.Generic.ICollection<T>.Add",
        "System.Collections.Generic.Dictionary.KeyCollection: System.Collections.Generic.ICollection<T>.Clear",
        "System.Collections.Generic.Dictionary.KeyCollection: System.Collections.Generic.ICollection<T>.Contains",
        "System.Collections.Generic.Dictionary.KeyCollection: System.Collections.Generic.ICollection<T>.CopyTo",
        "System.Collections.Generic.Dictionary.KeyCollection: System.Collections.Generic.ICollection<T>.Remove",
        "System.Collections.Generic.Dictionary.KeyCollection: System.Collections.Generic.ICollection<T>.get_IsReadOnly",
        "System.Collections.Generic.Dictionary.KeyCollection: System.Collections.ICollection.CopyTo",
        "System.Collections.Generic.Dictionary.KeyCollection: System.Collections.ICollection.get_IsSynchronized",
        "System.Collections.Generic.Dictionary.KeyCollection: System.Collections.ICollection.get_SyncRoot",
        "System.Collections.Generic.Dictionary.ValueCollection: System.Collections.Generic.ICollection<T>.Add",
        "System.Collections.Generic.Dictionary.ValueCollection: System.Collections.Generic.ICollection<T>.Clear",
        "System.Collections.Generic.Dictionary.ValueCollection: System.Collections.Generic.ICollection<T>.Contains",
        "System.Collections.Generic.Dictionary.ValueCollection: System.Collections.Generic.ICollection<T>.CopyTo",
        "System.Collections.Generic.Dictionary.ValueCollection: System.Collections.Generic.ICollection<T>.Remove",
        "System.Collections.Generic.Dictionary.ValueCollection: System.Collections.Generic.ICollection<T>.get_IsReadOnly",
        "System.Collections.Generic.Dictionary.ValueCollection: System.Collections.ICollection.CopyTo",
        "System.Collections.Generic.Dictionary.ValueCollection: System.Collections.ICollection.get_IsSynchronized",
        "System.Collections.Generic.Dictionary.ValueCollection: System.Collections.ICollection.get_SyncRoot",
        "System.Collections.Generic.Dictionary: System.Collections.Generic.ICollection<T>.Contains",
        "System.Collections.Generic.Dictionary: System.Collections.Generic.ICollection<T>.CopyTo",
        "System.Collections.Generic.Dictionary: System.Collections.Generic.ICollection<T>.get_IsReadOnly",
        "System.Collections.Generic.Dictionary: System.Collections.ICollection.CopyTo",
        "System.Collections.Generic.Dictionary: System.Collections.ICollection.get_IsSynchronized",
        "System.Collections.Generic.Dictionary: System.Collections.ICollection.get_SyncRoot",
        "System.Collections.Generic.Dictionary: System.Collections.IDictionary.get_IsFixedSize",
        "System.Collections.Generic.Dictionary: System.Collections.IDictionary.get_IsReadOnly",
        "System.Collections.Generic.Dictionary: System.Runtime.Serialization.IDeserializationCallback.OnDeserialization",
        "System.Collections.Generic.Dictionary: System.Runtime.Serialization.ISerializable.GetObjectData",
        "System.Collections.Generic.HashSet: System.Collections.Generic.ICollection<T>.CopyTo",
        "System.Collections.Generic.HashSet: System.Collections.Generic.ICollection<T>.get_IsReadOnly",
        "System.Collections.Generic.HashSet: System.Collections.Generic.IReadOnlySet<T>.IsProperSubsetOf",
        "System.Collections.Generic.HashSet: System.Collections.Generic.IReadOnlySet<T>.IsProperSupersetOf",
        "System.Collections.Generic.HashSet: System.Collections.Generic.IReadOnlySet<T>.IsSubsetOf",
        "System.Collections.Generic.HashSet: System.Collections.Generic.IReadOnlySet<T>.IsSupersetOf",
        "System.Collections.Generic.HashSet: System.Collections.Generic.IReadOnlySet<T>.Overlaps",
        "System.Collections.Generic.HashSet: System.Collections.Generic.IReadOnlySet<T>.SetEquals",
        "System.Collections.Generic.HashSet: System.Collections.Generic.ISet<T>.ExceptWith",
        "System.Collections.Generic.HashSet: System.Collections.Generic.ISet<T>.IntersectWith",
        "System.Collections.Generic.HashSet: System.Collections.Generic.ISet<T>.IsProperSubsetOf",
        "System.Collections.Generic.HashSet: System.Collections.Generic.ISet<T>.IsProperSupersetOf",
        "System.Collections.Generic.HashSet: System.Collections.Generic.ISet<T>.IsSubsetOf",
        "System.Collections.Generic.HashSet: System.Collections.Generic.ISet<T>.IsSupersetOf",
        "System.Collections.Generic.HashSet: System.Collections.Generic.ISet<T>.Overlaps",
        "System.Collections.Generic.HashSet: System.Collections.Generic.ISet<T>.SetEquals",
        "System.Collections.Generic.HashSet: System.Collections.Generic.ISet<T>.SymmetricExceptWith",
        "System.Collections.Generic.HashSet: System.Collections.Generic.ISet<T>.UnionWith",
        "System.Collections.Generic.HashSet: System.Runtime.Serialization.IDeserializationCallback.OnDeserialization",
        "System.Collections.Generic.HashSet: System.Runtime.Serialization.ISerializable.GetObjectData",
        "System.Collections.Generic.LinkedList: System.Collections.Generic.ICollection<T>.CopyTo",
        "System.Collections.Generic.LinkedList: System.Collections.Generic.ICollection<T>.get_IsReadOnly",
        "System.Collections.Generic.LinkedList: System.Collections.ICollection.CopyTo",
        "System.Collections.Generic.LinkedList: System.Collections.ICollection.get_IsSynchronized",
        "System.Collections.Generic.LinkedList: System.Collections.ICollection.get_SyncRoot",
        "System.Collections.Generic.LinkedList: System.Runtime.Serialization.IDeserializationCallback.OnDeserialization",
        "System.Collections.Generic.LinkedList: System.Runtime.Serialization.ISerializable.GetObjectData",
        "System.Collections.Generic.List: System.Collections.Generic.ICollection<T>.CopyTo",
        "System.Collections.Generic.List: System.Collections.Generic.ICollection<T>.get_IsReadOnly",
        "System.Collections.Generic.List: System.Collections.Generic.IList<T>.IndexOf",
        "System.Collections.Generic.List: System.Collections.ICollection.CopyTo",
        "System.Collections.Generic.List: System.Collections.ICollection.get_IsSynchronized",
        "System.Collections.Generic.List: System.Collections.ICollection.get_SyncRoot",
        "System.Collections.Generic.List: System.Collections.IList.IndexOf",
        "System.Collections.Generic.List: System.Collections.IList.get_IsFixedSize",
        "System.Collections.Generic.List: System.Collections.IList.get_IsReadOnly",
        "System.Collections.Generic.Queue: System.Collections.ICollection.CopyTo",
        "System.Collections.Generic.Queue: System.Collections.ICollection.get_IsSynchronized",
        "System.Collections.Generic.Queue: System.Collections.ICollection.get_SyncRoot",
        "System.Collections.Generic.Stack: System.Collections.ICollection.CopyTo",
        "System.Collections.Generic.Stack: System.Collections.ICollection.get_IsSynchronized",
        "System.Collections.Generic.Stack: System.Collections.ICollection.get_SyncRoot",
        "[,]: System.Collections.ICollection.CopyTo",
        "[,]: System.Collections.ICollection.get_IsSynchronized",
        "[,]: System.Collections.ICollection.get_SyncRoot",
        "[,]: System.Collections.IList.Clear",
        "[,]: System.Collections.IList.get_IsFixedSize",
        "[,]: System.Collections.IStructuralComparable.CompareTo",
        "[,]: System.Collections.IStructuralEquatable.Equals",
        "[,]: System.Collections.IStructuralEquatable.GetHashCode",
        "[,]: System.ICloneable.Clone",
        "[]: System.Collections.Generic.ICollection<T>.CopyTo",
        "[]: System.Collections.ICollection.CopyTo",
        "[]: System.Collections.ICollection.get_IsSynchronized",
        "[]: System.Collections.ICollection.get_SyncRoot",
        "[]: System.Collections.IList.Clear",
        "[]: System.Collections.IList.get_IsFixedSize",
        "[]: System.Collections.IStructuralComparable.CompareTo",
        "[]: System.Collections.IStructuralEquatable.Equals",
        "[]: System.Collections.IStructuralEquatable.GetHashCode",
        "[]: System.ICloneable.Clone"
    ];

    [Fact]
    public async Task Every_interface_member_of_a_table_type_or_an_array_is_decided_or_named_opaque()
    {
        var undecided = await Undecided();

        Assert.Empty(undecided.Except(Opaque).Order(StringComparer.Ordinal));
        Assert.Empty(Opaque.Except(undecided).Order(StringComparer.Ordinal));
    }

    /// <summary>The members of the interface map of each kind the map leaves undecided on an object of that kind.</summary>
    private static async Task<IReadOnlyList<string>> Undecided()
    {
        var compilation = await FixtureSolution.Create(("Case.cs", "public sealed class C { }")).Projects.Single().GetCompilationAsync() ??
                          throw new InvalidOperationException();
        var objectType = compilation.GetSpecialType(SpecialType.System_Object);
        var kinds = Types.Select(pair => (pair.Kind, Type: (ITypeSymbol)(compilation.GetTypeByMetadataName(pair.MetadataName) ??
                                                                         throw new InvalidOperationException(pair.MetadataName))))
                         .Append((CollectionObjects.ARRAY, compilation.CreateArrayTypeSymbol(objectType, 1)))
                         .Append((CollectionObjects.MULTIDIMENSIONAL_ARRAY, compilation.CreateArrayTypeSymbol(objectType, 2)));
        var undecided = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (kind, type) in kinds)
        foreach (var @interface in type.AllInterfaces)
        foreach (var member in @interface.GetMembers().OfType<IMethodSymbol>().Where(member => !member.IsStatic))
        {
            if (CollectionObjects.Decision(IrLowering.Collections.ImplementationsOf(member, compilation), kind) is null)
                undecided.Add($"{kind}: {@interface.OriginalDefinition.ToDisplayString()}.{member.Name}");
        }

        return undecided.ToArray();
    }

    private static readonly (string Kind, string MetadataName)[] Types =
    [
        ("System.Collections.Generic.List", "System.Collections.Generic.List`1"),
        ("System.Collections.Generic.Dictionary", "System.Collections.Generic.Dictionary`2"),
        ("System.Collections.Concurrent.ConcurrentDictionary", "System.Collections.Concurrent.ConcurrentDictionary`2"),
        ("System.Collections.Concurrent.ConcurrentQueue", "System.Collections.Concurrent.ConcurrentQueue`1"),
        ("System.Collections.Concurrent.ConcurrentStack", "System.Collections.Concurrent.ConcurrentStack`1"),
        ("System.Collections.Concurrent.ConcurrentBag", "System.Collections.Concurrent.ConcurrentBag`1"),
        ("System.Collections.Generic.HashSet", "System.Collections.Generic.HashSet`1"),
        ("System.Collections.Generic.Queue", "System.Collections.Generic.Queue`1"),
        ("System.Collections.Generic.Stack", "System.Collections.Generic.Stack`1"),
        ("System.Collections.Generic.LinkedList", "System.Collections.Generic.LinkedList`1"),
        ("System.Collections.Generic.Dictionary.KeyCollection", "System.Collections.Generic.Dictionary`2+KeyCollection"),
        ("System.Collections.Generic.Dictionary.ValueCollection", "System.Collections.Generic.Dictionary`2+ValueCollection")
    ];
}
