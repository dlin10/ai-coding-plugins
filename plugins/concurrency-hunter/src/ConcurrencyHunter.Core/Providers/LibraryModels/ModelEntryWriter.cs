using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConcurrencyHunter.Providers.LibraryModels.Generation;

namespace ConcurrencyHunter.Providers.LibraryModels;

internal static class ModelEntryWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n"
    };

    /// <summary>The entry a model is written as, in the project entry form. A generation passes its checks, which cancel the writing at
    /// each fate, effect, store, output, keep and call it writes.</summary>
    /// <param name="model">The model.</param>
    /// <param name="checks">The generation's checks, or <c>null</c> for a writing that nothing cancels.</param>
    internal static JsonObject Write(LibraryModel model, GenerationChecks? checks = null)
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
                                   group => group.Key, group => group.Select(effect => Effect(effect.Kind)), checks)
        };
        if (model.Result is { } result)
            entry["result"] = ResultText(result);
        if (model.Fates.Count != 0)
        {
            var fates = new JsonObject();
            foreach (var fate in model.Fates.OrderBy(fate => fate.Parameter, StringComparer.Ordinal))
            {
                WorkCancellation.Check();
                checks?.Check(GenerationStage.BuildModel);
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
                                      pair => pair.Value.Select(value => value.Canonical), checks);
        if (model.Outputs.Count != 0)
        {
            var outputs = new JsonObject();
            foreach (var output in model.Outputs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                WorkCancellation.Check();
                checks?.Check(GenerationStage.BuildModel);
                outputs[output.Key] = ResultText(output.Value);
            }
            entry["outputs"] = outputs;
        }
        if (model.Keeps.Count != 0)
            entry["keeps"] = Values(model.Keeps.OrderBy(pair => pair.Key, StringComparer.Ordinal), pair => pair.Key,
                                     pair => pair.Value.Select(value => value.Canonical), checks);
        if (model.Calls.Count != 0)
        {
            var calls = new JsonArray();
            foreach (var call in model.Calls.DistinctBy(call => call.Canonical).OrderBy(call => call.Canonical, StringComparer.Ordinal))
            {
                WorkCancellation.Check();
                checks?.Check(GenerationStage.BuildModel);
                var value = new JsonObject { ["member"] = call.Member, ["on"] = call.On.Canonical };
                if (call.Inputs.Count != 0)
                    value["inputs"] = new JsonArray(call.Inputs.Select(input => TextArray(input.Select(item => item.Canonical))).ToArray());
                calls.Add(value);
            }
            entry["calls"] = calls;
        }
        return entry;
    }

    /// <summary>The project model file a model is written as: one entry.</summary>
    /// <param name="model">The model.</param>
    /// <param name="checks">The generation's checks, which cancel the writing of the entry, or <c>null</c> for a writing that nothing
    /// cancels.</param>
    internal static byte[] File(LibraryModel model, GenerationChecks? checks = null)
    {
        var file = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["models"] = new JsonArray(Write(model, checks))
        };
        return Encoding.UTF8.GetBytes(file.ToJsonString(JsonOptions) + "\n");
    }

    /// <summary>Writes a result with recursive set ordering, retaining a dictionary's key and value positions.</summary>
    /// <param name="result">The result to write.</param>
    private static string ResultText(LibraryResult result)
    {
        WorkCancellation.Check();
        return result.Kind switch
        {
            LibraryResultKind.OneOf => $"[{LibraryResult.Set(result.Values)}]",
            LibraryResultKind.Sequence => $"sequence({LibraryResult.Set(result.Values)})",
            LibraryResultKind.Collection => $"collection({LibraryResult.Set(result.Values)})",
            LibraryResultKind.Dictionary => $"dictionary({string.Join(',', result.Values.Select(value => value.Canonical))})",
            LibraryResultKind.New => "new",
            LibraryResultKind.Task => $"task({ResultText(result.Inner!)})",
            _ => throw new UnreachableException($"Unknown result kind {result.Kind}.")
        };
    }

    private static JsonObject Values<T>(IEnumerable<T> entries, Func<T, string> key, Func<T, IEnumerable<string>> values, GenerationChecks? checks)
    {
        var result = new JsonObject();
        foreach (var entry in entries)
        {
            WorkCancellation.Check();
            checks?.Check(GenerationStage.BuildModel);
            result[key(entry)] = TextArray(values(entry));
        }
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

