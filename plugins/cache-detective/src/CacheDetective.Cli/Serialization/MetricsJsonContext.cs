using System.Text.Json.Serialization;

namespace CacheDetective.Serialization;

/// <summary>
/// The measurement and sample records, source-generated the way the Cli contract requires: nothing the
/// command writes or reads goes through the reflection serializer.
/// <para>It is a context of its own rather than more entries on <see cref="CacheDetectiveJsonContext"/>
/// because these files are written to disk and committed, and the two need different options. The tool
/// contract's context drops nulls and writes compactly; a sample file keeps its <c>"verdict": null</c>
/// and <c>"metrics": null</c> so that a labeller can see the empty slots, and is indented so that a
/// person can read and review the diff.</para>
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(Measurement))]
[JsonSerializable(typeof(MetricsSample))]
[JsonSerializable(typeof(CoverageMetric))]
[JsonSerializable(typeof(MeasurementCounts))]
[JsonSerializable(typeof(MeasurementCoverage))]
[JsonSerializable(typeof(SampleRow))]
[JsonSerializable(typeof(SampleScores))]
internal sealed partial class MetricsJsonContext : JsonSerializerContext;
