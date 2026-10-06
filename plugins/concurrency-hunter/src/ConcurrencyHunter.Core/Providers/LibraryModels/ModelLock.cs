using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels;

internal sealed class ModelLock
{
    private const string RELATIVE_PATH = ".concurrency-hunter/models/models.lock.json";
    private readonly string _path;
    private readonly bool _available;
    private readonly bool _keepRemoved;
    private readonly IReadOnlySet<string> _namedPatterns;
    private readonly Dictionary<(string Pattern, string Assembly, string Version), string[]> _rows = new();
    private readonly List<string> _diagnostics = new();
    private byte[]? _original;

    private ModelLock(string repositoryRoot, ProjectModelFiles files)
    {
        _path = Path.Combine(repositoryRoot, ".concurrency-hunter", "models", "models.lock.json");
        _available = files.ModelsFolderAvailable;
        _keepRemoved = files.Rejections.Any(rejection => rejection.Entry is null);
        _namedPatterns = files.NamedPatterns;
        if (!_available)
            return;
        try
        {
            _original = File.ReadAllBytes(_path);
            ReadRows(_original);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                                     JsonException or FormatException or DecoderFallbackException)
        {
            _rows.Clear();
            _diagnostics.Add($"library-models: {RELATIVE_PATH}: cannot read lock: {error.Message}");
        }
    }

    internal static ModelLock Read(string repositoryRoot, ProjectModelFiles files) => new(repositoryRoot, files);

    internal IReadOnlyList<string> Diagnostics => _diagnostics;

    internal IReadOnlyList<string> Members(string pattern, IAssemblySymbol assembly, Compilation compilation)
    {
        var key = (pattern, assembly.Identity.Name, assembly.Identity.Version.ToString());
        if (_rows.TryGetValue(key, out var locked))
            return locked;
        var memberName = PatternName(pattern);
        var typeName = PatternType(pattern);
        var types = DocumentationCommentId.GetSymbolsForDeclarationId("T:" + typeName, compilation)
                                         .OfType<INamedTypeSymbol>()
                                         .Where(type => SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, assembly));
        var members = types.SelectMany(type => type.GetMembers()
                                                 .OfType<IMethodSymbol>()
                                                 .Concat(type.GetMembers().OfType<IPropertySymbol>()
                                                             .Select(property => property.GetMethod)
                                                             .OfType<IMethodSymbol>()))
                           .Where(method => MethodName(method) == memberName)
                           .Select(method => DocumentationCommentId.CreateDeclarationId(method.OriginalDefinition))
                           .OfType<string>()
                           .Distinct(StringComparer.Ordinal)
                           .Order(StringComparer.Ordinal)
                           .ToArray();
        _rows.Add(key, members);
        return members;
    }

    internal void Write()
    {
        if (!_available)
            return;
        if (!_keepRemoved)
            foreach (var key in _rows.Keys.Where(key => !_namedPatterns.Contains(key.Pattern)).ToArray())
                _rows.Remove(key);
        if (_rows.Count == 0 && _original is null && _diagnostics.Count == 0)
            return;
        var rows = _rows.OrderBy(row => row.Key.Pattern, StringComparer.Ordinal)
                        .ThenBy(row => row.Key.Assembly, StringComparer.Ordinal)
                        .ThenBy(row => row.Key.Version, StringComparer.Ordinal)
                        .Select(row => new
                        {
                            pattern = row.Key.Pattern,
                            assembly = row.Key.Assembly,
                            version = row.Key.Version,
                            members = row.Value.Order(StringComparer.Ordinal).ToArray()
                        }).ToArray();
        var json = JsonSerializer.Serialize(new { schemaVersion = 1, patterns = rows },
                                            new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
        var bytes = new UTF8Encoding(false).GetBytes(json);
        if (_original is not null && _original.AsSpan().SequenceEqual(bytes))
            return;
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(_path))
                File.Replace(temporary, _path, null);
            else
                File.Move(temporary, _path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _diagnostics.Add($"library-models: {RELATIVE_PATH}: cannot write lock: {error.Message}");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static bool Matches(string pattern, string member) =>
        BuiltInModelReader.MemberId(member) is { Member: { } parsed } id && id.TypeName == PatternType(pattern) &&
        parsed.Name == PatternName(pattern);

    private void ReadRows(byte[] bytes)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.StartsWith('\uFEFF'))
            text = text[1..];
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Properties(root, "schemaVersion", "patterns");
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
            !root.TryGetProperty("patterns", out var patterns) || patterns.ValueKind != JsonValueKind.Array)
            throw new FormatException("lock needs schemaVersion 1 and a patterns array.");
        foreach (var row in patterns.EnumerateArray())
        {
            Properties(row, "pattern", "assembly", "version", "members");
            if (!row.TryGetProperty("pattern", out var patternValue) || patternValue.ValueKind != JsonValueKind.String ||
                patternValue.GetString() is not { } pattern || !ProjectModelFiles.IsPattern(pattern) ||
                !row.TryGetProperty("assembly", out var assemblyValue) || assemblyValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(assemblyValue.GetString()) ||
                !row.TryGetProperty("version", out var versionValue) || versionValue.ValueKind != JsonValueKind.String ||
                !FourPartVersion(versionValue.GetString(), out var version) ||
                !row.TryGetProperty("members", out var memberValues) || memberValues.ValueKind != JsonValueKind.Array)
                throw new FormatException("lock row needs a pattern, assembly, four-part version and members array.");
            var members = memberValues.EnumerateArray().ToArray();
            if (members.Any(member => member.ValueKind != JsonValueKind.String ||
                                      !Matches(pattern, member.GetString()!)))
                throw new FormatException("lock row has a member outside its pattern.");
            if (!_rows.TryAdd((pattern, assemblyValue.GetString()!, version.ToString()),
                              members.Select(member => member.GetString()!).ToArray()))
                throw new FormatException("lock has two rows for one pattern, assembly and version.");
        }
    }

    private static void Properties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("expected a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name) || !names.Contains(property.Name, StringComparer.Ordinal))
                throw new FormatException($"repeated or unknown property '{property.Name}'.");
        if (seen.Count != names.Length)
            throw new FormatException("object is missing a required property.");
    }

    private static bool FourPartVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (value is null || !Regex.IsMatch(value, @"^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(value, out var parsed))
            return false;
        version = parsed;
        return true;
    }

    private static string PatternType(string pattern) => pattern[2..pattern.LastIndexOf('.', pattern.Length - 4)];
    private static string PatternName(string pattern) => pattern[(pattern.LastIndexOf('.', pattern.Length - 4) + 1)..^3];
    private static string MethodName(IMethodSymbol method) => method.MethodKind switch
    {
        MethodKind.Constructor => "#ctor",
        MethodKind.StaticConstructor => "#cctor",
        _ => method.Name
    };
}
