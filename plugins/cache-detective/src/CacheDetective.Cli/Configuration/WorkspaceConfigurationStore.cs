using System.Text.Json;
using System.Text.Json.Serialization;
using CacheDetective.Serialization;

namespace CacheDetective.Configuration;

public static class WorkspaceConfigurationStore
{
    private const string ConfigurationDirectory = ".cache-detective";
    private const string ConfigurationFile = "workspace.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
    private static readonly CacheDetectiveJsonContext SerializerContext = new(SerializerOptions);

    public static string GetPath(string repositoryRoot) =>
        Path.Combine(Path.GetFullPath(repositoryRoot), ConfigurationDirectory, ConfigurationFile);

    public static async Task<WorkspaceConfiguration> ReadAsync(string repositoryRoot,
                                                                CancellationToken cancellationToken = default)
    {
        var path = GetPath(repositoryRoot);
        await using var stream = File.OpenRead(path);
        var configuration = await JsonSerializer.DeserializeAsync(
            stream, SerializerContext.WorkspaceConfiguration, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Workspace configuration '{path}' is empty.");

        EnsureSupportedVersion(configuration.Version);
        return configuration;
    }

    public static async Task<bool> WriteAsync(string repositoryRoot,
                                              WorkspaceConfiguration configuration,
                                              CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureSupportedVersion(configuration.Version);

        var path = GetPath(repositoryRoot);
        var json = JsonSerializer.Serialize(configuration, SerializerContext.WorkspaceConfiguration);
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
}
