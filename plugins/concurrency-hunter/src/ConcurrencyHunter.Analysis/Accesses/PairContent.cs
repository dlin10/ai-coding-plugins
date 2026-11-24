using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Di;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Accesses;

/// <summary>The order of two accesses, or of two variants of one access, by what they hold (ADR 0042). Within one execution the order
/// the fold first met two accesses is one another execution can decide, so wherever pairing needs one of two copies of one execution
/// first this order gives it: the side of a pair of one execution reported, filtered and keyed by orientation, the next execution a
/// witness's search takes, and the order of found pairs of one execution.</summary>
internal static class PairContent
{
    /// <summary>Compares two accesses field by field: first what a finding's group reads of the resource (scope, assembly, region,
    /// path and member), then the site, the instance and the operation, then every other field the fold tells accesses apart by, so that
    /// two accesses compare equal exactly where the fold keeps them as one (<see cref="AccessFolding.AccessKey"/>).</summary>
    /// <param name="x">One access.</param>
    /// <param name="y">The other.</param>
    internal static int Compare(Access x, Access y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        var (one, other) = (x.Resource, y.Resource);
        // One resource object is one content, which is not read again: a member's text is joined anew at each read.
        var oneResource = ReferenceEquals(one, other);
        int result;
        if ((!oneResource && ((result = string.CompareOrdinal(one.Scope, other.Scope)) != 0 ||
                              (result = string.CompareOrdinal(one.Assembly, other.Assembly)) != 0 ||
                              (result = string.CompareOrdinal(one.RegionId ?? one.Region, other.RegionId ?? other.Region)) != 0 ||
                              (result = Strings(one.AccessPath, other.AccessPath)) != 0 ||
                              (result = MemberIdentity(one.Member, other.Member)) != 0)) ||
            (result = string.CompareOrdinal(x.BodyId, y.BodyId)) != 0 ||
            (result = x.OperationId.CompareTo(y.OperationId)) != 0 ||
            (result = string.CompareOrdinal(x.InstanceId, y.InstanceId)) != 0 ||
            (result = x.Operation.CompareTo(y.Operation)) != 0 ||
            (result = string.CompareOrdinal(x.Symbol, y.Symbol)) != 0 ||
            (result = Span(x.Source, y.Source)) != 0 ||
            (result = Strings(x.HeldProtection, y.HeldProtection)) != 0 ||
            (result = Strings(x.HeldProtectionIds, y.HeldProtectionIds)) != 0 ||
            (result = Strings(x.Uncertainties, y.Uncertainties)) != 0 ||
            (result = Strings(x.OwnershipEvidence, y.OwnershipEvidence)) != 0 ||
            (result = x.Ownership.CompareTo(y.Ownership)) != 0 ||
            (result = x.IsReferenceAccess.CompareTo(y.IsReferenceAccess)) != 0 ||
            (result = x.IsIterationIndexed.CompareTo(y.IsIterationIndexed)) != 0 ||
            (result = x.IsFresh.CompareTo(y.IsFresh)) != 0 ||
            (result = x.IsLibraryInternal.CompareTo(y.IsLibraryInternal)) != 0 ||
            (result = x.WritesWholeStruct.CompareTo(y.WritesWholeStruct)) != 0 ||
            (result = x.IsConstructionLocal.CompareTo(y.IsConstructionLocal)) != 0 ||
            (result = Optional(x.UnseenWriteOperation, y.UnseenWriteOperation)) != 0 ||
            (!oneResource && (result = Resource(one, other)) != 0) ||
            (result = Held(x.HeldProtections, y.HeldProtections)) != 0 ||
            (result = Sequence(x.BindingEvidence, y.BindingEvidence, Binding)) != 0 ||
            (result = Sequence(x.Conditions, y.Conditions, Condition)) != 0 ||
            (result = Term(x.SelectorTerm, y.SelectorTerm)) != 0 ||
            (result = Sequence(x.GapChecks, y.GapChecks, Check)) != 0)
            return result;
        return Sequence(x.DecidingCalls, y.DecidingCalls, Deciding);
    }

    /// <summary>Compares two accesses as one execution each has them: the fields the fold compares (<see cref="Compare(Access, Access)"/>),
    /// then the execution, then the variant, so that two views compare equal only where they are one execution's of one access.</summary>
    /// <param name="x">One view.</param>
    /// <param name="y">The other.</param>
    internal static int CompareViews(Access x, Access y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (Compare(x, y) is var access and not 0)
            return access;
        if (string.CompareOrdinal(x.ExecutionId, y.ExecutionId) is var execution and not 0)
            return execution;
        return Compare(new AccessVariant(x.Root, x.PathRoot, x.CallPath, x.SpawnSites, x.CodeFlow, x.ReadSources),
                       new AccessVariant(y.Root, y.PathRoot, y.CallPath, y.SpawnSites, y.CodeFlow, y.ReadSources));
    }

    /// <summary>Compares two variants of one access field by field: the roots' ids, the call path, the spawn sites, the code flow and
    /// the read sources, each sequence item by item and then by length, then the rest of each root, so that no two different contents
    /// compare equal.</summary>
    /// <param name="x">One variant.</param>
    /// <param name="y">The other.</param>
    internal static int Compare(AccessVariant x, AccessVariant y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        int result;
        if ((result = string.CompareOrdinal(x.PathRoot.RootId, y.PathRoot.RootId)) != 0 ||
            (result = string.CompareOrdinal(x.Root.RootId, y.Root.RootId)) != 0 ||
            (result = Strings(x.CallPath, y.CallPath)) != 0 ||
            (result = Sequence(x.SpawnSites, y.SpawnSites, Site)) != 0 ||
            (result = Sequence(x.CodeFlow, y.CodeFlow, Step)) != 0 ||
            (result = Sequence(x.ReadSources, y.ReadSources, Read)) != 0 ||
            (result = Root(x.PathRoot, y.PathRoot)) != 0)
            return result;
        return Root(x.Root, y.Root);
    }

    /// <summary>Two roots by every field their records are equal by.</summary>
    /// <param name="x">One root.</param>
    /// <param name="y">The other.</param>
    private static int Root(AccessRoot x, AccessRoot y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        int result;
        if ((result = string.CompareOrdinal(x.RootId, y.RootId)) != 0 || (result = string.CompareOrdinal(x.Symbol, y.Symbol)) != 0 ||
            (result = string.CompareOrdinal(x.Display, y.Display)) != 0 || (result = string.CompareOrdinal(x.ProviderId, y.ProviderId)) != 0 ||
            (result = string.CompareOrdinal(x.RootKind, y.RootKind)) != 0 ||
            (result = x.Policy.Multiplicity.CompareTo(y.Policy.Multiplicity)) != 0 ||
            (result = x.Policy.SelfOverlap.CompareTo(y.Policy.SelfOverlap)) != 0 ||
            (result = string.CompareOrdinal(x.Policy.ScopeBinding, y.Policy.ScopeBinding)) != 0 ||
            (result = string.CompareOrdinal(x.Scope, y.Scope)) != 0 || (result = string.CompareOrdinal(x.ReceiverType, y.ReceiverType)) != 0)
            return result;
        return string.CompareOrdinal(x.ReceiverTypeKey, y.ReceiverTypeKey);
    }

    /// <summary>What else of two resources the fold compares, beyond what a finding's group reads.</summary>
    /// <param name="x">One resource.</param>
    /// <param name="y">The other.</param>
    private static int Resource(AccessResource x, AccessResource y)
    {
        int result;
        if ((result = string.CompareOrdinal(x.Region, y.Region)) != 0 || (result = string.CompareOrdinal(x.RegionId, y.RegionId)) != 0 ||
            (result = string.CompareOrdinal(x.RegionKey, y.RegionKey)) != 0 ||
            (result = string.CompareOrdinal(x.CollectionId, y.CollectionId)) != 0 || (result = x.IsWildcard.CompareTo(y.IsWildcard)) != 0 ||
            (result = Member(x.Member, y.Member)) != 0 ||
            // A selector's record text names its kind and every field; equal selectors are not formatted.
            (result = x.Selector == y.Selector ? 0 : string.CompareOrdinal(x.Selector?.ToString(), y.Selector?.ToString())) != 0 ||
            (result = Sequence(x.StructPath, y.StructPath, Member)) != 0 || (result = Storage(x.Storage, y.Storage)) != 0)
            return result;
        // Equal sets of exclusions, most often the one empty set, are not sorted.
        return ReferenceEquals(x.WildcardExclusions, y.WildcardExclusions) || x.WildcardExclusions.SetEquals(y.WildcardExclusions)
            ? 0
            : Strings(x.WildcardExclusions.Order(StringComparer.Ordinal).ToArray(), y.WildcardExclusions.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Two members in the ordinal order of their identities, the text joined only where the two members differ.</summary>
    /// <param name="x">One member.</param>
    /// <param name="y">The other.</param>
    private static int MemberIdentity(MemberKey x, MemberKey y) => x == y ? 0 : string.CompareOrdinal(x.Identity, y.Identity);

    private static int Member(MemberKey x, MemberKey y)
    {
        int result;
        if ((result = string.CompareOrdinal(x.DeclaringType, y.DeclaringType)) != 0 || (result = string.CompareOrdinal(x.Name, y.Name)) != 0 ||
            (result = x.Kind.CompareTo(y.Kind)) != 0)
            return result;
        return string.CompareOrdinal(x.DeclaringTypeIdentity, y.DeclaringTypeIdentity);
    }

    private static int Storage(StoragePath? x, StoragePath? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null || y is null)
            return x is null ? -1 : 1;
        int result;
        if ((result = Sequence(x.Fields, y.Fields, Field)) != 0 || (result = x.Cell.CompareTo(y.Cell)) != 0 ||
            (result = string.CompareOrdinal(x.Region, y.Region)) != 0)
            return result;
        return string.CompareOrdinal(x.Slot, y.Slot);
    }

    /// <summary>Two fields by every field their records are equal by, the volatility, the containing type's readonliness and the
    /// field type's key among them.</summary>
    /// <param name="x">One field.</param>
    /// <param name="y">The other.</param>
    private static int Field(IrFieldRef x, IrFieldRef y)
    {
        int result;
        if ((result = string.CompareOrdinal(x.Assembly, y.Assembly)) != 0 || (result = string.CompareOrdinal(x.ContainingType, y.ContainingType)) != 0 ||
            (result = string.CompareOrdinal(x.Name, y.Name)) != 0 || (result = x.Kind.CompareTo(y.Kind)) != 0 ||
            (result = x.IsStatic.CompareTo(y.IsStatic)) != 0 || (result = x.IsReadOnly.CompareTo(y.IsReadOnly)) != 0 ||
            (result = string.CompareOrdinal(x.Type, y.Type)) != 0 ||
            (result = string.CompareOrdinal(x.ContainingTypeIdentity, y.ContainingTypeIdentity)) != 0 ||
            (result = x.IsVolatile.CompareTo(y.IsVolatile)) != 0 || (result = x.IsContainingTypeReadOnly.CompareTo(y.IsContainingTypeReadOnly)) != 0)
            return result;
        return string.CompareOrdinal(x.FieldTypeKey, y.FieldTypeKey);
    }

    /// <summary>Two accesses' protections by object: the objects' ids in ordinal order, then what each is held by.</summary>
    /// <param name="x">One access's protections.</param>
    /// <param name="y">The other's.</param>
    private static int Held(IReadOnlyDictionary<string, IReadOnlyList<HeldProtection>> x, IReadOnlyDictionary<string, IReadOnlyList<HeldProtection>> y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        var (ids, otherIds) = (x.Keys.Order(StringComparer.Ordinal).ToArray(), y.Keys.Order(StringComparer.Ordinal).ToArray());
        if (Strings(ids, otherIds) is var result and not 0)
            return result;
        // bounded: the objects one access holds.
        foreach (var id in ids)
        {
            WorkCancellation.Check();
            if (Sequence(x[id], y[id], Protection) is var held and not 0)
                return held;
        }

        return 0;
    }

    private static int Protection(HeldProtection x, HeldProtection y)
    {
        int result;
        if ((result = x.Primitive.CompareTo(y.Primitive)) != 0 || (result = x.Mode.CompareTo(y.Mode)) != 0)
            return result;
        return x.IsExclusive.CompareTo(y.IsExclusive);
    }

    private static int Binding(BindingEvidence x, BindingEvidence y)
    {
        int result;
        if ((result = string.CompareOrdinal(x.Kind, y.Kind)) != 0 || (result = string.CompareOrdinal(x.Text, y.Text)) != 0)
            return result;
        return Span(x.Source, y.Source);
    }

    /// <summary>Two conditions by every field their records are equal by: the subject, relation, value and text, then the subject's
    /// term, which a collector may bind to an empty subject, and the subject's width and sign.</summary>
    /// <param name="x">One condition.</param>
    /// <param name="y">The other.</param>
    private static int Condition(PathPredicate x, PathPredicate y)
    {
        int result;
        if ((result = string.CompareOrdinal(x.Subject, y.Subject)) != 0 || (result = x.Relation.CompareTo(y.Relation)) != 0 ||
            (result = string.CompareOrdinal(x.Value, y.Value)) != 0 || (result = string.CompareOrdinal(x.Text, y.Text)) != 0 ||
            (result = Term(x.SubjectTerm, y.SubjectTerm)) != 0 || (result = x.Width.CompareTo(y.Width)) != 0)
            return result;
        return x.Signed.CompareTo(y.Signed);
    }

    /// <summary>Two value terms by kind, then field by field, the operands of a compound term in turn.</summary>
    /// <param name="x">One term, or null.</param>
    /// <param name="y">The other, or null.</param>
    private static int Term(ValueTerm? x, ValueTerm? y)
    {
        WorkCancellation.Check();
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null || y is null)
            return x is null ? -1 : 1;
        if (string.CompareOrdinal(x.GetType().Name, y.GetType().Name) is var kind and not 0)
            return kind;
        return (x, y) switch
        {
            (ConstantTerm a, ConstantTerm b) => First(a.Value.CompareTo(b.Value), a.Width.CompareTo(b.Width), a.Signed.CompareTo(b.Signed)),
            (VariableTerm a, VariableTerm b) => First(string.CompareOrdinal(a.Identity, b.Identity), a.Width.CompareTo(b.Width), a.Signed.CompareTo(b.Signed)),
            (SumTerm a, SumTerm b) => Term(a.Left, b.Left) is var left and not 0 ? left : Term(a.Right, b.Right),
            (ConvertTerm a, ConvertTerm b) => Term(a.Operand, b.Operand) is var operand and not 0
                ? operand
                : First(a.Width.CompareTo(b.Width), a.Signed.CompareTo(b.Signed)),
            // A kind this order does not know yet: its record text names every field.
            _ => string.CompareOrdinal(x.ToString(), y.ToString())
        };
    }

    private static int Check(GapCheck x, GapCheck y)
    {
        int result;
        if ((result = string.CompareOrdinal(x.Callee, y.Callee)) != 0 || (result = string.CompareOrdinal(x.Kind, y.Kind)) != 0 ||
            (result = string.CompareOrdinal(x.Component, y.Component)) != 0)
            return result;
        return string.CompareOrdinal(x.Reason, y.Reason);
    }

    private static int Deciding(DecidingCall x, DecidingCall y)
    {
        if (Check(x.Check, y.Check) is var check and not 0)
            return check;
        if (ReferenceEquals(x.Call, y.Call))
            return 0;
        if (x.Call is null || y.Call is null)
            return x.Call is null ? -1 : 1;
        int result;
        if ((result = string.CompareOrdinal(x.Call.BodyId, y.Call.BodyId)) != 0 || (result = Span(x.Call.Span, y.Call.Span)) != 0 ||
            (result = string.CompareOrdinal(x.Call.Target, y.Call.Target)) != 0)
            return result;
        return x.Call.Ordinal.CompareTo(y.Call.Ordinal);
    }

    private static int Strings(IReadOnlyList<string> x, IReadOnlyList<string> y) => Sequence(x, y, string.CompareOrdinal);

    private static int Sequence<T>(IReadOnlyList<T> x, IReadOnlyList<T> y, Func<T, T, int> item)
    {
        if (ReferenceEquals(x, y))
            return 0;
        // bounded: the shorter of two lists an access or a variant holds.
        for (var at = 0; at < Math.Min(x.Count, y.Count); at++)
        {
            if (item(x[at], y[at]) is var result and not 0)
                return result;
        }

        return x.Count.CompareTo(y.Count);
    }

    private static int Optional<T>(T? x, T? y) where T : struct, Enum =>
        x is null || y is null ? (x is null).CompareTo(y is null) * -1 : Comparer<T>.Default.Compare(x.Value, y.Value);

    private static int First(params int[] results) => results.FirstOrDefault(result => result != 0);

    private static int Span(SourceSpan x, SourceSpan y)
    {
        int result;
        if ((result = string.CompareOrdinal(x.Path, y.Path)) != 0 || (result = x.StartLine.CompareTo(y.StartLine)) != 0 ||
            (result = x.StartColumn.CompareTo(y.StartColumn)) != 0 || (result = x.EndLine.CompareTo(y.EndLine)) != 0)
            return result;
        return x.EndColumn.CompareTo(y.EndColumn);
    }

    private static int Site(SpawnSiteLocation x, SpawnSiteLocation y) =>
        string.CompareOrdinal(x.Segment, y.Segment) is var result and not 0 ? result : Span(x.Source, y.Source);

    private static int Step(CodeFlowStep x, CodeFlowStep y)
    {
        int result;
        if ((result = string.CompareOrdinal(x.Kind, y.Kind)) != 0 || (result = string.CompareOrdinal(x.Text, y.Text)) != 0)
            return result;
        return Span(x.Source, y.Source);
    }

    private static int Read(ReadSource x, ReadSource y)
    {
        int result;
        if ((result = string.CompareOrdinal(x.Symbol, y.Symbol)) != 0 || (result = Span(x.Source, y.Source)) != 0)
            return result;
        return Sequence(x.CodeFlow, y.CodeFlow, Step);
    }
}
