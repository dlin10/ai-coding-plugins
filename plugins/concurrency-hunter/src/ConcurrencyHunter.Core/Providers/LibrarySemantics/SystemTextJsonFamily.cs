using static ConcurrencyHunter.Providers.LibrarySemantics.LibraryEffect;

namespace ConcurrencyHunter.Providers.LibrarySemantics;

/// <summary><c>System.Text.Json</c> (R2): serialization reads its value deep, deserialization from a string touches nothing. The
/// overloads over streams, writers, readers, pipes, spans, nodes, elements, documents, type infos and contexts are not known.</summary>
internal static class SystemTextJsonFamily
{
    private static readonly SupportedAssemblyVersion[] Assemblies = [SupportedAssemblyVersion.Framework("System.Text.Json")];

    internal static IEnumerable<LibraryMember> Members =>
    [
        Known("M:System.Text.Json.JsonSerializer.Serialize``1(``0,System.Text.Json.JsonSerializerOptions)~System.String", DeepReadOf("value")),
        Known("M:System.Text.Json.JsonSerializer.Serialize(System.Object,System.Type,System.Text.Json.JsonSerializerOptions)~System.String",
              DeepReadOf("value")),
        Known("M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes``1(``0,System.Text.Json.JsonSerializerOptions)~System.Byte[]",
              DeepReadOf("value")),
        Known("M:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(System.Object,System.Type,System.Text.Json.JsonSerializerOptions)~System.Byte[]",
              DeepReadOf("value")),
        Known("M:System.Text.Json.JsonSerializer.Deserialize``1(System.String,System.Text.Json.JsonSerializerOptions)~``0"),
        Known("M:System.Text.Json.JsonSerializer.Deserialize(System.String,System.Type,System.Text.Json.JsonSerializerOptions)~System.Object"),
        Known("M:System.Text.Json.JsonSerializerOptions.#ctor"),
        Known("M:System.Text.Json.JsonSerializerOptions.#ctor(System.Text.Json.JsonSerializerDefaults)"),
        Known("M:System.Text.Json.JsonDocument.Parse(System.String,System.Text.Json.JsonDocumentOptions)~System.Text.Json.JsonDocument"),
        Known("M:System.Text.Json.JsonDocument.get_RootElement~System.Text.Json.JsonElement"),
        Known("M:System.Text.Json.JsonElement.GetProperty(System.String)~System.Text.Json.JsonElement"),
        Known("M:System.Text.Json.JsonElement.ToString~System.String"),
        Known("M:System.Text.Json.JsonElement.EnumerateArray~System.Text.Json.JsonElement.ArrayEnumerator"),
        Known("M:System.Text.Json.Utf8JsonReader.get_TokenType~System.Text.Json.JsonTokenType"),
        Known("M:System.Text.Json.Utf8JsonReader.GetInt32~System.Int32"),
        Known("M:System.Text.Json.Utf8JsonReader.GetString~System.String"),
        Known("M:System.Text.Json.JsonException.#ctor"),
        Known("M:System.Text.Json.JsonException.#ctor(System.String)"),
        Known("M:System.Text.Json.JsonException.#ctor(System.String,System.Exception)"),
        Known("M:System.Text.Json.JsonException.#ctor(System.String,System.String,System.Nullable{System.Int64},System.Nullable{System.Int64})"),
        Known("M:System.Text.Json.JsonException.#ctor(System.String,System.String,System.Nullable{System.Int64},System.Nullable{System.Int64},System.Exception)"),
        Known("M:System.Text.Json.JsonException.#ctor(System.Runtime.Serialization.SerializationInfo,System.Runtime.Serialization.StreamingContext)")
    ];

    private static LibraryMember Known(string id, params LibraryEffect[] effects) => new(id, Assemblies, effects);
}
