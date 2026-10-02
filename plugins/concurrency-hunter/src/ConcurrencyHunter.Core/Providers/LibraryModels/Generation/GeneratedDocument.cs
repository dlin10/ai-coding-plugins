using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The generator's answer as the document <c>generate</c> writes (G-6): UTF-8 without BOM, LF, a final newline, every
/// property always present. Returns the bytes; writing them is the caller's.</summary>
public static class GeneratedDocument
{
    /// <summary>The document's bytes.</summary>
    /// <param name="answer">The answer.</param>
    public static byte[] Write(GeneratedAnswer answer)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", answer.SchemaVersion);
            json.WriteString("member", answer.Member);
            json.WriteStartObject("assembly");
            json.WriteString("name", answer.Assembly.Name);
            json.WriteString("version", answer.Assembly.Version);
            json.WriteString("package", answer.Assembly.Package);
            json.WriteEndObject();
            if (answer.Classified is { } classified)
            {
                json.WriteStartObject("classified");
                foreach (var (parameter, fate) in classified)
                {
                    json.WriteStartObject(parameter);
                    json.WriteString("fate", fate.Fate);
                    json.WriteString("holder", fate.Holder);
                    json.WriteEndObject();
                }
                json.WriteEndObject();
            }
            else
                json.WriteNull("classified");
            json.WriteString("reason", answer.Reason);
            WriteGeneration(json, answer.Generation);
            json.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteGeneration(Utf8JsonWriter json, GenerationRecord generation)
    {
        json.WriteStartObject("generation");
        if (generation.Implementation is { } implementation)
        {
            json.WriteStartObject("implementation");
            json.WriteString("path", implementation.Path);
            json.WriteString("assemblyVersion", implementation.AssemblyVersion);
            json.WriteString("fileVersion", implementation.FileVersion);
            json.WriteString("mvid", implementation.Mvid);
            json.WriteEndObject();
        }
        else
            json.WriteNull("implementation");
        json.WriteString("framework", generation.Framework);
        WriteStrings(json, "missingDependencies", generation.MissingDependencies);
        json.WriteNumber("externBodies", generation.ExternBodies);
        WriteStrings(json, "defaultedValues", generation.DefaultedValues);
        json.WriteStartObject("refusals");
        foreach (var (parameter, refusal) in generation.Refusals)
            json.WriteString(parameter, refusal);
        json.WriteEndObject();
        WriteStrings(json, "setupWidened", generation.SetupWidened);
        json.WriteNumber("reachedBodies", generation.ReachedBodies);
        // Always one decimal, as G-6's examples write it: 0.0, not 0.
        json.WritePropertyName("seconds");
        json.WriteRawValue(Math.Round(generation.Seconds, 1).ToString("0.0", CultureInfo.InvariantCulture));
        json.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, IEnumerable<string> values)
    {
        json.WriteStartArray(name);
        foreach (var value in values)
            json.WriteStringValue(value);
        json.WriteEndArray();
    }
}
