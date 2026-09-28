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
    private static readonly Regex TypeIdText = new(@"^T:[A-Za-z_][A-Za-z0-9_]*(?:`[0-9]+)?(?:[.+][A-Za-z_][A-Za-z0-9_]*(?:`[0-9]+)?)*$", RegexOptions.CultureInvariant);

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
                members.Add(new LibraryModel(entry.Member, assemblies.Select(name => new SupportedAssemblyVersion(name, version.Value.Minimum,
                                                                                                          version.Value.Maximum)).ToArray(), effects));
            }
            var types = new List<ImmutableLibraryType>();
            foreach (var entry in file.ImmutableTypes ?? [])
            {
                if (entry is null || entry.Type is null || !TypeIdText.IsMatch(entry.Type))
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

    internal static bool IsMemberId(string id)
    {
        if (!id.StartsWith("M:", StringComparison.Ordinal) || id.EndsWith("(*)", StringComparison.Ordinal) ||
            id.Any(char.IsWhiteSpace))
            return false;
        var returnType = id.IndexOf('~');
        if (returnType >= 0 && (returnType == id.Length - 1 || id[(returnType + 1)..].Contains('~')))
            return false;
        var signature = id.IndexOfAny(['(', '~']);
        var name = signature < 0 ? id[2..] : id[2..signature];
        var separator = name.LastIndexOf('.');
        if (separator <= 0 || separator == name.Length - 1 || !TypeIdText.IsMatch("T:" + name[..separator]) ||
            name[(separator + 1)..].StartsWith("set_", StringComparison.Ordinal))
            return false;
        if ((name.EndsWith(".op_Implicit", StringComparison.Ordinal) || name.EndsWith(".op_Explicit", StringComparison.Ordinal)) &&
            !id.Contains('~'))
            return false;
        var open = id.IndexOf('(');
        var close = id.IndexOf(')');
        if (open < 0)
            return close < 0;
        if (close <= open || id.LastIndexOf('(') != open || id.LastIndexOf(')') != close ||
            close != id.Length - 1 && id[close + 1] != '~')
            return false;
        var braces = 0;
        var brackets = 0;
        var needsParameter = true;
        foreach (var character in id.AsSpan(open + 1, close - open - 1))
        {
            if (character == '{') braces++;
            else if (character == '}' && --braces < 0) return false;
            else if (character == '[') brackets++;
            else if (character == ']' && --brackets < 0) return false;
            else if (character == ',' && braces == 0 && brackets == 0)
            {
                if (needsParameter)
                    return false;
                needsParameter = true;
                continue;
            }
            if (braces == 0 && brackets == 0)
                needsParameter = false;
        }
        return braces == 0 && brackets == 0 && !needsParameter;
    }

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
    public bool? Opaque { get; set; }
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
