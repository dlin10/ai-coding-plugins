using System.Text.Json.Serialization;
using Common.Mcp;
using ConcurrencyHunter.Mcp;
using ConcurrencyHunter.Runs;

namespace ConcurrencyHunter.Serialization;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, 
                                PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ErrorPayload))]
[JsonSerializable(typeof(StartPayload))]
[JsonSerializable(typeof(PollPayload))]
[JsonSerializable(typeof(SubmissionPayload))]
[JsonSerializable(typeof(RenderPayload))]
[JsonSerializable(typeof(RunCounts))]
[JsonSerializable(typeof(RenderCounts))]
[JsonSerializable(typeof(GroupDigest))]
[JsonSerializable(typeof(DigestFinding))]
[JsonSerializable(typeof(DigestAccess))]
[JsonSerializable(typeof(ListEnvelope<GroupDigest>))]
internal sealed partial class ConcurrencyHunterJsonContext : JsonSerializerContext;
