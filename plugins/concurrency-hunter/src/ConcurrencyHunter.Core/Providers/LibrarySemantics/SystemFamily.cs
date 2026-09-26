using static ConcurrencyHunter.Providers.LibrarySemantics.LibraryEffect;

namespace ConcurrencyHunter.Providers.LibrarySemantics;

/// <summary><c>System.*</c> (R2): the immutable types, whose members taking only immutable arguments are known without effect, and
/// the members with a mutable parameter the census met, each with its effect on that parameter. A type is declared in
/// <c>System.Runtime</c> in the reference assemblies a build compiles against and in <c>System.Private.CoreLib</c> or
/// <c>System.Private.Uri</c> at run time. <c>Object.Equals(Object, Object)</c> is absent: it calls an overridden <c>Equals</c>.</summary>
internal static class SystemFamily
{
    private static readonly SupportedAssemblyVersion[] Assemblies =
    [
        SupportedAssemblyVersion.Framework("System.Runtime"),
        SupportedAssemblyVersion.Framework("System.Private.CoreLib"),
        SupportedAssemblyVersion.Framework("System.Private.Uri")
    ];

    internal static IEnumerable<ImmutableLibraryType> ImmutableTypes =>
    [
        Immutable("T:System.String"),
        Immutable("T:System.Boolean"),
        Immutable("T:System.Char"),
        Immutable("T:System.SByte"),
        Immutable("T:System.Byte"),
        Immutable("T:System.Int16"),
        Immutable("T:System.UInt16"),
        Immutable("T:System.Int32"),
        Immutable("T:System.UInt32"),
        Immutable("T:System.Int64"),
        Immutable("T:System.UInt64"),
        Immutable("T:System.Single"),
        Immutable("T:System.Double"),
        Immutable("T:System.Decimal"),
        Immutable("T:System.Guid"),
        Immutable("T:System.DateTime"),
        Immutable("T:System.DateTimeOffset"),
        Immutable("T:System.TimeSpan"),
        Immutable("T:System.DateOnly"),
        Immutable("T:System.TimeOnly"),
        Immutable("T:System.Uri"),
        Immutable("T:System.Version"),
        Immutable("T:System.Nullable`1"),
        Immutable("T:System.Collections.Generic.KeyValuePair`2"),
        Immutable("T:System.Type"),
        Immutable("T:System.Enum"),
        Immutable("T:System.Math"),
        Immutable("T:System.Convert"),
        Immutable("T:System.StringComparer"),
        Immutable("T:System.Text.Encoding"),
        new("T:System.Exception", Assemblies, IncludesDerived: true)
    ];

    internal static IEnumerable<LibraryMember> Members =>
    [
        Known("M:System.Object.#ctor"),
        Known("M:System.Object.ReferenceEquals(System.Object,System.Object)~System.Boolean"),
        Known("M:System.Object.GetType~System.Type"),
        Known("M:System.Environment.get_ProcessorCount~System.Int32"),
        Known("M:System.Environment.get_TickCount~System.Int32"),
        // Not met by the census: the exception phase 5b makes (TD-034a). Tests use it as a sink that keeps a value alive, and what
        // it does is known exactly, so it is no unknown call.
        Known("M:System.GC.KeepAlive(System.Object)"),
        Known("M:System.String.Concat(System.String[])~System.String", DeepReadOf("values")),
        Known("M:System.String.Concat(System.ReadOnlySpan{System.String})~System.String", DeepReadOf("values")),
        Known("M:System.Text.Encoding.GetString(System.Byte[])~System.String", DeepReadOf("bytes")),
        Known("M:System.Text.Encoding.GetString(System.ReadOnlySpan{System.Byte})~System.String", DeepReadOf("bytes")),
        Known("M:System.Array.IndexOf``1(``0[],``0)~System.Int32", DeepReadOf("array"), DeepReadOf("value"))
    ];

    private static ImmutableLibraryType Immutable(string id) => new(id, Assemblies);

    private static LibraryMember Known(string id, params LibraryEffect[] effects) => new(id, Assemblies, effects);
}
