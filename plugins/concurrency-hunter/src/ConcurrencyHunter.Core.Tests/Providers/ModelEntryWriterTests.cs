using System.Text;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class ModelEntryWriterTests
{
    private const string MEMBER = "M:Example.Api.Run(System.Object[],System.Action{System.Object},System.Object@)";

    [Fact]
    public void Every_built_in_entry_round_trips_through_the_project_reader()
    {
        foreach (var model in LibraryModels.BuiltIn.Members)
        {
            var files = ProjectModelFiles.Read("model.json", ModelEntryWriter.File(model));
            var entry = Assert.Single(files.Entries);

            Assert.Empty(files.Rejections);
            Assert.Equal(model.Id, entry.Member);
            Assert.Equal(model.Assemblies.Select(range => range.AssemblyName).Order(StringComparer.Ordinal),
                         entry.Assemblies.Order(StringComparer.Ordinal));
            var versions = Assert.NotNull(entry.Versions);
            Assert.All(model.Assemblies, range => Assert.Equal((range.Minimum, range.MaximumExclusive), versions));
            Assert.True(model.Effects.ToHashSet().SetEquals(entry.Effects));
            Assert.True(LibraryVocabulary.Alike(model.Result, model.Fates, entry.Result, entry.Fates, model.Stores, entry.Stores,
                                                model.Outputs, entry.Outputs, model.Keeps, entry.Keeps), model.Id);
        }
    }

    [Fact]
    public void A_complete_entry_has_the_expected_bytes()
    {
        const string EXPECTED = """
            {
              "schemaVersion": 1,
              "models": [
                {
                  "member": "M:Example.Api.Run(System.Object[],System.Action{System.Object},System.Object@)",
                  "assemblies": [
                    "A",
                    "B"
                  ],
                  "versions": {
                    "minimum": "1.0.0.0",
                    "maximumExclusive": "2.0.0.0"
                  },
                  "effects": {
                    "items": [
                      "reads-deep",
                      "writes-cells"
                    ],
                    "output": [
                      "reads-deep"
                    ]
                  },
                  "result": "[arg:items]",
                  "fates": {
                    "callback": {
                      "fate": "holder",
                      "holder": "this",
                      "inputs": [
                        [
                          "arg:items",
                          "arg:output"
                        ]
                      ]
                    }
                  },
                  "stores": {
                    "items": [
                      "arg:items",
                      "arg:output"
                    ]
                  },
                  "outputs": {
                    "output": "[arg:items]"
                  },
                  "keeps": {
                    "this": [
                      "arg:items",
                      "arg:output"
                    ]
                  }
                }
              ]
            }
            """;

        Assert.Equal(EXPECTED.ReplaceLineEndings("\n") + "\n", Encoding.UTF8.GetString(ModelEntryWriter.File(Model(reverse: true))));
    }

    [Fact]
    public void Entry_and_fate_properties_have_the_fixed_order()
    {
        var entry = ModelEntryWriter.Write(Model(reverse: false));
        var fate = entry["fates"]!["callback"]!.AsObject();

        Assert.Equal(["member", "assemblies", "versions", "effects", "result", "fates", "stores", "outputs", "keeps"],
                     entry.Select(property => property.Key));
        Assert.Equal(["fate", "holder", "inputs"], fate.Select(property => property.Key));
    }

    [Fact]
    public void Equal_models_built_in_different_orders_have_the_same_bytes()
    {
        Assert.Equal(ModelEntryWriter.File(Model(reverse: false)), ModelEntryWriter.File(Model(reverse: true)));
    }

    [Theory]
    [InlineData("[arg:items,arg:output]", "[arg:output,arg:items]")]
    [InlineData("sequence(sequence(arg:items,arg:output),arg:items)", "sequence(arg:items,sequence(arg:output,arg:items))")]
    [InlineData("collection(grouping(arg:output,sequence(arg:items,arg:output)),arg:items)", "collection(arg:items,grouping(arg:output,sequence(arg:output,arg:items)))")]
    [InlineData("dictionary(sequence(arg:items,arg:output),arg:items)", "dictionary(sequence(arg:output,arg:items),arg:items)")]
    public void Recursive_set_orders_in_results_and_outputs_have_identical_bytes(string first, string second)
    {
        var a = WithResultAndOutput(first);
        var b = WithResultAndOutput(second);

        Assert.Equal(ModelEntryWriter.File(a), ModelEntryWriter.File(b));
        Assert.Empty(ProjectModelFiles.Read("model.json", ModelEntryWriter.File(a)).Rejections);
    }

    [Theory]
    [InlineData("dictionary(arg:items,arg:output)", "dictionary(arg:output,arg:items)")]
    [InlineData("[grouping(arg:items,arg:output)]", "[grouping(arg:output,arg:items)]")]
    public void Dictionary_and_grouping_positions_remain_distinct(string first, string second) =>
        Assert.NotEqual(ModelEntryWriter.File(WithResultAndOutput(first)), ModelEntryWriter.File(WithResultAndOutput(second)));

    /// <summary>Builds a model with the same nested result in both observable destinations.</summary>
    /// <param name="text">The result text.</param>
    private static LibraryModel WithResultAndOutput(string text) => new(MEMBER, [new("A", new Version(1, 0, 0, 0), new Version(2, 0, 0, 0))], [])
    {
        Result = LibraryResult.Parse(text),
        Outputs = new Dictionary<string, LibraryResult> { ["output"] = LibraryResult.Parse(text)! }
    };

    private static LibraryModel Model(bool reverse)
    {
        var items = new ArgumentValue("items");
        var output = new ArgumentValue("output");
        IReadOnlyList<SupportedAssemblyVersion> assemblies =
        [
            new("A", new Version(1, 0, 0, 0), new Version(2, 0, 0, 0)),
            new("B", new Version(1, 0, 0, 0), new Version(2, 0, 0, 0))
        ];
        IReadOnlyList<LibraryEffect> effects =
        [
            LibraryEffect.DeepReadOf("items"),
            LibraryEffect.WriteCellsOf("items"),
            LibraryEffect.DeepReadOf("output")
        ];
        var values = reverse ? new LibraryValue[] { output, items } : [items, output];
        var stores = new Dictionary<string, IReadOnlyList<LibraryValue>> { ["items"] = values };
        var outputs = new Dictionary<string, LibraryResult> { ["output"] = LibraryResult.Parse("[arg:items]")! };
        var keeps = new Dictionary<string, IReadOnlyList<LibraryValue>> { ["this"] = values };
        return new LibraryModel(MEMBER, reverse ? assemblies.Reverse().ToArray() : assemblies,
                                reverse ? effects.Reverse().ToArray() : effects)
        {
            Result = LibraryResult.Parse("[arg:items]"),
            Fates = [new LibraryFate("callback", LibraryFateKind.Holder, LibraryHolderKind.This, [values])],
            Stores = stores,
            Outputs = outputs,
            Keeps = keeps
        };
    }
}
