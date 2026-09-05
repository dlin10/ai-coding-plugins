using System.Text.Json;
using System.Text.Json.Serialization;
using CacheDetective.Serialization;
using CacheDetective.Graph;

namespace CacheDetective.Configuration;

public static class WorkspaceConfigurationStore
{
    private const string CONFIGURATION_DIRECTORY = ".cache-detective";
    private const string CONFIGURATION_FILE = "workspace.json";

    private static readonly JsonSerializerOptions SERIALIZER_OPTIONS = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private static readonly CacheDetectiveJsonContext SERIALIZER_CONTEXT = new(SERIALIZER_OPTIONS);

    public static string GetPath(string repositoryRoot) =>
        Path.Combine(Path.GetFullPath(repositoryRoot), CONFIGURATION_DIRECTORY, CONFIGURATION_FILE);

    public static async Task<WorkspaceConfiguration> ReadAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        return await ReadFileAsync(GetPath(repositoryRoot), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<WorkspaceConfiguration> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        WorkspaceConfiguration configuration;
        try
        {
            configuration = await JsonSerializer.DeserializeAsync(stream, SERIALIZER_CONTEXT.WorkspaceConfiguration, cancellationToken).ConfigureAwait(false) ??
                            throw new InvalidDataException($"Workspace configuration '{path}' is empty.");
        }
        catch (JsonException error) when (error.Path?.StartsWith("$.services", StringComparison.Ordinal) == true)
        {
            throw new InvalidDataException("services maps a client name to a solution or project name, as in \"catalog\": \"Catalog.API\"", error);
        }

        EnsureSupportedVersion(configuration.Version);
        EnsureSupportedDatabases(configuration);
        EnsureSupportedEvents(configuration);
        return configuration;
    }

    public static async Task<bool> WriteAsync(string repositoryRoot, WorkspaceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureSupportedVersion(configuration.Version);
        EnsureSupportedDatabases(configuration);
        EnsureSupportedEvents(configuration);

        var path = GetPath(repositoryRoot);
        var json = JsonSerializer.Serialize(configuration, SERIALIZER_CONTEXT.WorkspaceConfiguration);
        if (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            using var existingDocument = JsonDocument.Parse(existing);
            using var newDocument = JsonDocument.Parse(json);
            if (JsonElement.DeepEquals(existingDocument.RootElement, newDocument.RootElement))
            {
                return false;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void EnsureSupportedVersion(int version)
    {
        if (version != WorkspaceConfiguration.CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported workspace configuration version {version}.");
        }
    }

    /// <summary>A workspace scans at most one database, because a <c>Table</c> is identified by
    /// <c>schema.name</c> and two databases would collapse into one vertex; see <c>docs/adr/0007</c>.
    /// No database at all stays entirely legal.</summary>
    private static void EnsureSupportedDatabases(WorkspaceConfiguration configuration)
    {
        var databases = configuration.Databases ?? [];
        if (databases.Length > 1)
        {
            var names = string.Join(", ", databases.Select(database => database.Name ?? "(unnamed)"));
            throw new InvalidDataException($"A workspace can scan one database, and this configuration names {databases.Length} " +
                                           $"({names}). A table is identified by schema.name, so two databases would collapse into " +
                                           "one vertex; see docs/adr/0007.");
        }

        foreach (var database in databases)
        {
            database.EnsureSupported();
        }
    }

    private static void EnsureSupportedEvents(WorkspaceConfiguration configuration)
    {
        foreach (var @event in configuration.Events ?? [])
            @event.ToRecognizer(Confidence.Confirmed, null);
    }
}
