using System.Text.Json.Serialization;
using Common.Mcp;

namespace Common.Tests;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ListEnvelope<string>))]
internal partial class TestJsonContext : JsonSerializerContext;
