using System.Text.Json.Serialization;

namespace ConcurrencyHunter.Serialization;

/// <summary>The <c>metrics</c> measurement and the demo snapshot, source-generated like the run tool payloads.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
                             PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             WriteIndented = true)]
[JsonSerializable(typeof(Measurement))]
[JsonSerializable(typeof(MeasurementSnapshot))]
internal sealed partial class MetricsJsonContext : JsonSerializerContext;
