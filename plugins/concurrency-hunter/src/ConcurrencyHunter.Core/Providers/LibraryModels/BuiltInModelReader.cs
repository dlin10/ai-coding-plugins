using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ConcurrencyHunter.Providers.LibraryModels;

/// <summary>Reads a built-in library model from its embedded UTF-8 bytes.</summary>
internal static class BuiltInModelReader
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex VersionText = new(@"^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant);

    internal static (IReadOnlyList<LibraryModel> Members, IReadOnlyList<ImmutableLibraryType> ImmutableTypes) Read(byte[] bytes)
    {
        try
        {
            var text = Utf8.GetString(bytes);
            if (text.StartsWith('\uFEFF'))
                text = text[1..];
            using var document = JsonDocument.Parse(text);
            CheckDuplicates(document.RootElement);
            var file = JsonSerializer.Deserialize(text, LibraryModelJsonContext.Default.ModelFile);
            if (file is not { SchemaVersion: 1, Models: not null })
                throw new LibraryModelException("A built-in model needs schemaVersion 1 and a models array.");
            var defaults = Assemblies(file.Assemblies);
            (Version Minimum, Version Maximum)? defaultVersion = file.Versions is null ? null : Version(file.Versions);
            var members = new List<LibraryModel>();
            foreach (var entry in file.Models)
            {
                if (entry is null || entry.Member is null || !IsMemberId(entry.Member) || entry.Opaque is not null || entry.Effects is null)
                    throw new LibraryModelException("A built-in model entry needs an exact member and effects, and cannot be opaque.");
                var assemblies = Assemblies(entry.Assemblies) ?? defaults;
                var version = entry.Versions is null ? defaultVersion : Version(entry.Versions);
                if (assemblies is null || version is null)
                    throw new LibraryModelException($"{entry.Member} needs an assembly and a version range.");
                var effects = new List<LibraryEffect>();
                foreach (var (parameter, kinds) in entry.Effects)
                {
                    if (string.IsNullOrWhiteSpace(parameter) || kinds is not { Length: > 0 } || kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Length)
                        throw new LibraryModelException($"{entry.Member} has invalid effects.");
                    foreach (var kind in kinds)
                        effects.Add(kind switch
                        {
                            "reads-deep" => LibraryEffect.DeepReadOf(parameter),
                            "writes-arg" => LibraryEffect.WriteOf(parameter),
                            _ => throw new LibraryModelException($"{entry.Member} has an unknown effect kind.")
                        });
                }
                var (result, fates) = LibraryVocabulary.Entry(entry.Result, (entry.Fates ?? []).Select(fate =>
                    new RawFate(fate.Key, fate.Value.Fate, fate.Value.Holder, fate.Value.Inputs)).ToArray());
                members.Add(new LibraryModel(entry.Member, assemblies.Select(name => new SupportedAssemblyVersion(name, version.Value.Minimum,
                                                                                                          version.Value.Maximum)).ToArray(), effects)
                {
                    Result = result,
                    Fates = fates
                });
            }
            var types = new List<ImmutableLibraryType>();
            foreach (var entry in file.ImmutableTypes ?? [])
            {
                if (entry is null || entry.Type is null || DeclarationId.Parse(entry.Type) is not { IsType: true })
                    throw new LibraryModelException("An immutable type needs a T: declaration id.");
                var assemblies = Assemblies(entry.Assemblies) ?? defaults;
                var version = entry.Versions is null ? defaultVersion : Version(entry.Versions);
                if (assemblies is null || version is null)
                    throw new LibraryModelException($"{entry.Type} needs an assembly and a version range.");
                types.Add(new ImmutableLibraryType(entry.Type, assemblies.Select(name => new SupportedAssemblyVersion(name, version.Value.Minimum,
                                                                                                               version.Value.Maximum)).ToArray(), entry.IncludesDerived ?? false));
            }
            return (members, types);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or FormatException or OverflowException)
        {
            throw new LibraryModelException($"Invalid built-in library model: {exception.Message}");
        }
    }

    private static string[]? Assemblies(string[]? names)
    {
        if (names is null)
            return null;
        if (names.Length == 0 || names.Any(string.IsNullOrWhiteSpace))
            throw new LibraryModelException("An assembly list needs non-empty names.");
        return names;
    }

    internal static bool IsMemberId(string id) => MemberId(id) is not null;

    /// <summary>The parsed member id, or null when <paramref name="id"/> is not one or breaks a member rule.</summary>
    internal static DeclarationId? MemberId(string id) =>
        DeclarationId.Parse(id) is { IsMember: true, Member: { } member } parsed && FollowsMemberRules(member) ? parsed : null;

    /// <summary>No setter, and a conversion operator only with its return type.</summary>
    internal static bool FollowsMemberRules(IdMember member) =>
        !member.Name.StartsWith("set_", StringComparison.Ordinal) &&
        !(member.Name is "op_Implicit" or "op_Explicit" && member.Arity == 0 && member.ReturnType is null);

    private static (Version Minimum, Version Maximum) Version(ModelVersionRange range)
    {
        if (range.Minimum is null || range.MaximumExclusive is null || !VersionText.IsMatch(range.Minimum) ||
            !VersionText.IsMatch(range.MaximumExclusive) || !System.Version.TryParse(range.Minimum, out var minimum) ||
            !System.Version.TryParse(range.MaximumExclusive, out var maximum) || minimum >= maximum)
            throw new LibraryModelException("A version range needs four-part versions with its minimum below its maximum.");
        return (minimum, maximum);
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            throw new LibraryModelException("A built-in library model cannot contain null.");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new LibraryModelException($"Repeated JSON property '{property.Name}'.");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CheckDuplicates(item);
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ModelFile
{
    public int SchemaVersion { get; set; }
    public string? Note { get; set; }
    public string[]? Assemblies { get; set; }
    public ModelVersionRange? Versions { get; set; }
    public ModelEntry[]? Models { get; set; }
    public ImmutableTypeEntry[]? ImmutableTypes { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ModelEntry
{
    public string? Member { get; set; }
    public string[]? Assemblies { get; set; }
    public ModelVersionRange? Versions { get; set; }
    public Dictionary<string, string[]>? Effects { get; set; }
    public string? Result { get; set; }
    public Dictionary<string, ModelFateEntry>? Fates { get; set; }
    public bool? Opaque { get; set; }
    public string? Note { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ModelFateEntry
{
    public string? Fate { get; set; }
    public string? Holder { get; set; }
    public string[][]? Inputs { get; set; }
    public string? Note { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ImmutableTypeEntry
{
    public string? Type { get; set; }
    public bool? IncludesDerived { get; set; }
    public string[]? Assemblies { get; set; }
    public ModelVersionRange? Versions { get; set; }
    public string? Note { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ModelVersionRange
{
    public string? Minimum { get; set; }
    public string? MaximumExclusive { get; set; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ModelFile))]
internal sealed partial class LibraryModelJsonContext : JsonSerializerContext;
