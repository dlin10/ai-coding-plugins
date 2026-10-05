using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConcurrencyHunter.Providers.LibraryModels;

internal static class ModelEntryWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n"
    };

    internal static JsonObject Write(LibraryModel model)
    {
        var ranges = model.Assemblies.OrderBy(range => range.AssemblyName, StringComparer.Ordinal).ToArray();
        if (ranges.Length == 0 || ranges.Any(range => range.Minimum != ranges[0].Minimum ||
                                                    range.MaximumExclusive != ranges[0].MaximumExclusive))
            throw new ArgumentException("A model entry needs assemblies with one version range.", nameof(model));

        var entry = new JsonObject
        {
            ["member"] = model.Id,
            ["assemblies"] = new JsonArray(ranges.Select(range => JsonValue.Create(range.AssemblyName)).ToArray()),
            ["versions"] = new JsonObject
            {
                ["minimum"] = ranges[0].Minimum.ToString(4),
                ["maximumExclusive"] = ranges[0].MaximumExclusive.ToString(4)
            },
            ["effects"] = Values(model.Effects.GroupBy(effect => effect.Parameter, StringComparer.Ordinal)
                                               .OrderBy(group => group.Key, StringComparer.Ordinal),
                                   group => group.Key, group => group.Select(effect => Effect(effect.Kind)))
        };
        if (model.Result is { } result)
            entry["result"] = ResultText(result);
        if (model.Fates.Count != 0)
        {
            var fates = new JsonObject();
            foreach (var fate in model.Fates.OrderBy(fate => fate.Parameter, StringComparer.Ordinal))
            {
                var value = new JsonObject { ["fate"] = LibraryFate.Text(fate.Kind) };
                if (fate.Holder is { } holder)
                    value["holder"] = holder == LibraryHolderKind.Result ? "result" : "this";
                if (fate.Inputs is { Count: > 0 } inputs && inputs.Any(input => input.Count != 0))
                    value["inputs"] = new JsonArray(inputs.Select(input => TextArray(input.Select(item => item.Canonical))).ToArray());
                fates[fate.Parameter] = value;
            }
            entry["fates"] = fates;
        }
        if (model.Stores.Count != 0)
            entry["stores"] = Values(model.Stores.OrderBy(pair => pair.Key, StringComparer.Ordinal), pair => pair.Key,
                                      pair => pair.Value.Select(value => value.Canonical));
        if (model.Outputs.Count != 0)
        {
            var outputs = new JsonObject();
            foreach (var output in model.Outputs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                outputs[output.Key] = ResultText(output.Value);
            entry["outputs"] = outputs;
        }
        if (model.Keeps.Count != 0)
            entry["keeps"] = Values(model.Keeps.OrderBy(pair => pair.Key, StringComparer.Ordinal), pair => pair.Key,
                                     pair => pair.Value.Select(value => value.Canonical));
        return entry;
    }

    internal static byte[] File(LibraryModel model)
    {
        var file = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["models"] = new JsonArray(Write(model))
        };
        return Encoding.UTF8.GetBytes(file.ToJsonString(JsonOptions) + "\n");
    }

    /// <summary>Writes a result with recursive set ordering, retaining a dictionary's key and value positions.</summary>
    /// <param name="result">The result to write.</param>
    private static string ResultText(LibraryResult result) => result.Kind switch
    {
        LibraryResultKind.OneOf => $"[{LibraryResult.Set(result.Values)}]",
        LibraryResultKind.Sequence => $"sequence({LibraryResult.Set(result.Values)})",
        LibraryResultKind.Collection => $"collection({LibraryResult.Set(result.Values)})",
        LibraryResultKind.Dictionary => $"dictionary({string.Join(',', result.Values.Select(value => value.Canonical))})",
        LibraryResultKind.New => "new",
        _ => throw new UnreachableException($"Unknown result kind {result.Kind}.")
    };

    private static JsonObject Values<T>(IEnumerable<T> entries, Func<T, string> key, Func<T, IEnumerable<string>> values)
    {
        var result = new JsonObject();
        foreach (var entry in entries)
            result[key(entry)] = TextArray(values(entry));
        return result;
    }

    private static JsonArray TextArray(IEnumerable<string> values) =>
        new(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(value => JsonValue.Create(value)).ToArray());

    private static string Effect(LibraryEffectKind kind) => kind switch
    {
        LibraryEffectKind.DeepRead => "reads-deep",
        LibraryEffectKind.WriteArgument => "writes-arg",
        LibraryEffectKind.WriteCells => "writes-cells",
        _ => throw new UnreachableException($"Unknown effect kind {kind}.")
    };
}

