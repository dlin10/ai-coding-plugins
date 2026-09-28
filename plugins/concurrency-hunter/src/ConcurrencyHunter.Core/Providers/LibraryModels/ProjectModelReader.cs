using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConcurrencyHunter.Providers.LibraryModels;

internal sealed record ProjectModelEntry(string Path, int Position, string Member, IReadOnlyList<string> Assemblies,
                                         (Version Minimum, Version Maximum)? Versions, IReadOnlyList<LibraryEffect> Effects,
                                         bool Opaque);

internal sealed record ModelRejection(string Path, string? Entry, string Reason)
{
    public string Diagnostic => $"library-models: {Path}: " + (Entry is null ? Reason : $"{Entry}: {Reason}");
}

internal sealed record ProjectModelFiles(IReadOnlyList<ProjectModelEntry> Entries, IReadOnlyList<ModelRejection> Rejections,
                                         IReadOnlySet<string> NamedPatterns, bool ModelsFolderAvailable)
{
    public static ProjectModelFiles Read(string repositoryRoot)
    {
        var entries = new List<ProjectModelEntry>();
        var rejections = new List<ModelRejection>();
        var namedPatterns = new HashSet<string>(StringComparer.Ordinal);
        var models = Path.Combine(repositoryRoot, ".concurrency-hunter", "models");
        try
        {
            if ((File.GetAttributes(models) & FileAttributes.Directory) == 0)
                rejections.Add(new ModelRejection(".concurrency-hunter/models", null, "models is not a directory."));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ProjectModelFiles(entries, rejections, namedPatterns, false);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            rejections.Add(new ModelRejection(".concurrency-hunter/models", null, $"cannot inspect folder: {error.Message}"));
            return new ProjectModelFiles(entries, rejections, namedPatterns, false);
        }
        if (rejections.Count != 0)
            return new ProjectModelFiles(entries, rejections, namedPatterns, false);

        var modelsFolderAvailable = true;
        void Walk(string folder)
        {
            var relative = Path.GetRelativePath(repositoryRoot, folder).Replace('\\', '/');
            string[] children;
            try
            {
                children = Directory.GetFileSystemEntries(folder);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                if (folder == models)
                    modelsFolderAvailable = false;
                rejections.Add(new ModelRejection(relative, null, $"cannot list folder: {error.Message}"));
                return;
            }

            foreach (var child in children.Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(child);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(child);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    rejections.Add(new ModelRejection(Path.GetRelativePath(repositoryRoot, child).Replace('\\', '/'), null,
                                                      $"cannot inspect path: {error.Message}"));
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) == 0 &&
                        (folder != models || name is not { } direct ||
                         !direct.Equals("generated", StringComparison.OrdinalIgnoreCase) &&
                         !direct.Equals("ai", StringComparison.OrdinalIgnoreCase)))
                        Walk(child);
                    continue;
                }
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                    (folder == models && name.Equals("models.lock.json", StringComparison.OrdinalIgnoreCase)))
                    continue;
                var path = Path.GetRelativePath(repositoryRoot, child).Replace('\\', '/');
                try
                {
                    ReadFile(File.ReadAllBytes(child), path, entries, rejections, namedPatterns);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    rejections.Add(new ModelRejection(path, null, $"cannot read file: {error.Message}"));
                }
            }
        }

        Walk(models);
        return new ProjectModelFiles(entries, rejections, namedPatterns, modelsFolderAvailable);
    }

    private static void ReadFile(byte[] bytes, string path, List<ProjectModelEntry> entries, List<ModelRejection> rejections,
                                 HashSet<string> namedPatterns)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.StartsWith('\uFEFF'))
                text = text[1..];
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var namedModels) &&
                namedModels.ValueKind == JsonValueKind.Array)
                foreach (var namedEntry in namedModels.EnumerateArray())
                    if (namedEntry.ValueKind == JsonValueKind.Object && namedEntry.TryGetProperty("member", out var namedMember) &&
                        namedMember.ValueKind == JsonValueKind.String && namedMember.GetString() is { } id && IsPattern(id))
                        namedPatterns.Add(id);
            Properties(root, ["schemaVersion", "note", "assemblies", "versions", "models", "immutableTypes"]);
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var version) || version != 1)
                throw new FormatException("schemaVersion must be 1.");
            if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                throw new FormatException("models must be an array.");
            if (root.TryGetProperty("note", out var note) && note.ValueKind != JsonValueKind.String)
                throw new FormatException("note must be a string.");
            var defaults = root.TryGetProperty("assemblies", out var assemblyNames) ? Assemblies(assemblyNames) : null;
            (Version Minimum, Version Maximum)? defaultVersions = root.TryGetProperty("versions", out var range) ? Versions(range) : null;
            if (root.TryGetProperty("immutableTypes", out var immutableTypes) && immutableTypes.ValueKind != JsonValueKind.Array)
                throw new FormatException("immutableTypes must be an array.");

            foreach (var (entry, position) in models.EnumerateArray().Select((entry, index) => (entry, index)))
            {
                string? member = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("member", out var id) &&
                                 id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                try
                {
                    entries.Add(ParseEntry(entry, path, position, defaults, defaultVersions));
                }
                catch (FormatException error)
                {
                    rejections.Add(new ModelRejection(path, member ?? $"models[{position}]", error.Message));
                }
            }
            if (root.TryGetProperty("immutableTypes", out immutableTypes))
            {
                foreach (var (_, position) in immutableTypes.EnumerateArray().Select((entry, index) => (entry, index)))
                    rejections.Add(new ModelRejection(path, $"immutableTypes[{position}]", "project immutable types are not supported."));
            }
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or FormatException)
        {
            rejections.Add(new ModelRejection(path, null, error.Message));
            return;
        }
    }

    private static ProjectModelEntry ParseEntry(JsonElement entry, string path, int position, IReadOnlyList<string>? defaults,
                                                (Version Minimum, Version Maximum)? defaultVersions)
    {
        Properties(entry, ["member", "assemblies", "versions", "effects", "opaque", "note"]);
        if (!entry.TryGetProperty("member", out var memberValue) || memberValue.ValueKind != JsonValueKind.String ||
            memberValue.GetString() is not { } member ||
            !BuiltInModelReader.IsMemberId(member) && !IsPattern(member))
            throw new FormatException("member must be an M: declaration id or a member pattern.");
        var assemblies = entry.TryGetProperty("assemblies", out var assemblyNames) ? Assemblies(assemblyNames) : defaults;
        if (assemblies is null)
            throw new FormatException("entry needs at least one assembly.");
        var versions = entry.TryGetProperty("versions", out var range) ? Versions(range) : defaultVersions;
        if (entry.TryGetProperty("note", out var note) && note.ValueKind != JsonValueKind.String)
            throw new FormatException("note must be a string.");
        var hasEffects = entry.TryGetProperty("effects", out var effectsValue);
        var hasOpaque = entry.TryGetProperty("opaque", out var opaqueValue);
        if (hasEffects == hasOpaque)
            throw new FormatException("entry needs exactly one of effects and opaque.");
        if (hasOpaque && opaqueValue.ValueKind != JsonValueKind.True)
            throw new FormatException("opaque must be true.");
        var effects = new List<LibraryEffect>();
        if (hasEffects)
        {
            Properties(effectsValue, null);
            foreach (var effect in effectsValue.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(effect.Name) || effect.Value.ValueKind != JsonValueKind.Array)
                    throw new FormatException("effects must map a parameter to a non-empty array of kinds.");
                var kinds = effect.Value.EnumerateArray().ToArray();
                if (kinds.Length == 0)
                    throw new FormatException("effect kind list cannot be empty.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kind in kinds)
                {
                    if (kind.ValueKind != JsonValueKind.String || kind.GetString() is not { } name || !seen.Add(name))
                        throw new FormatException("effect kinds must be distinct strings.");
                    effects.Add(name switch
                    {
                        "reads-deep" => LibraryEffect.DeepReadOf(effect.Name),
                        "writes-arg" => LibraryEffect.WriteOf(effect.Name),
                        _ => throw new FormatException($"unknown effect kind '{name}'.")
                    });
                }
            }
        }
        return new ProjectModelEntry(path, position, member, assemblies, versions, effects, hasOpaque);
    }

    private static void Properties(JsonElement element, string[]? allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("expected a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new FormatException($"repeated property '{property.Name}'.");
            if (allowed is not null && !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new FormatException($"unknown property '{property.Name}'.");
        }
    }

    private static IReadOnlyList<string> Assemblies(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new FormatException("assemblies must be an array of non-empty strings.");
        var names = element.EnumerateArray().ToArray();
        if (names.Length == 0 || names.Any(name => name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())))
            throw new FormatException("assemblies must be an array of non-empty strings.");
        return names.Select(name => name.GetString()!).ToArray();
    }

    private static (Version Minimum, Version Maximum) Versions(JsonElement element)
    {
        Properties(element, ["minimum", "maximumExclusive"]);
        if (element.EnumerateObject().Count() != 2 || !element.TryGetProperty("minimum", out var minimumValue) ||
            !element.TryGetProperty("maximumExclusive", out var maximumValue) || minimumValue.ValueKind != JsonValueKind.String ||
            maximumValue.ValueKind != JsonValueKind.String || !FourParts(minimumValue.GetString(), out var minimum) ||
            !FourParts(maximumValue.GetString(), out var maximum) || minimum >= maximum)
            throw new FormatException("versions need four-part minimum and maximumExclusive with minimum below maximumExclusive.");
        return (minimum, maximum);
    }

    private static bool FourParts(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (value is null || !Regex.IsMatch(value, @"^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(value, out var parsed))
            return false;
        version = parsed;
        return true;
    }

    internal static bool IsPattern(string id) => id.StartsWith("M:", StringComparison.Ordinal) && id.EndsWith("(*)", StringComparison.Ordinal) &&
                                                !id[(id.LastIndexOf('.', id.Length - 4) + 1)..^3].Contains('`') &&
                                                BuiltInModelReader.IsMemberId(id[..^3] + "(System.Int32)");
}
