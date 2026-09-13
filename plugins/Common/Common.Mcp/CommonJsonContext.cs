using System.Text.Json.Serialization;

namespace Common.Mcp;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkspaceDiagnosticResult))]
[JsonSerializable(typeof(ListEnvelope<WorkspaceDiagnosticResult>))]
internal sealed partial class CommonJsonContext : JsonSerializerContext;
