using System.Text;
using ConcurrencyHunter.Providers.LibraryModels;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class BuiltInModelFileTests
{
    private const string Valid = """
        {"schemaVersion":1,"assemblies":["A"],"versions":{"minimum":"1.0.0.0","maximumExclusive":"2.0.0.0"},"models":[{"member":"M:A.B.Run","effects":{}}],"immutableTypes":[{"type":"T:A.B"}]}
        """;

    [Fact]
    public void Every_family_is_one_embedded_file()
    {
        var names = typeof(LibraryModels).Assembly.GetManifestResourceNames().Where(name => name.StartsWith("LibraryModels.BuiltIn.", StringComparison.Ordinal));
        Assert.Equal(["LibraryModels.BuiltIn.entity-framework.json", "LibraryModels.BuiltIn.http-client.json", "LibraryModels.BuiltIn.linq.json",
                      "LibraryModels.BuiltIn.logging.json", "LibraryModels.BuiltIn.newtonsoft-json.json", "LibraryModels.BuiltIn.system-text-json.json",
                      "LibraryModels.BuiltIn.system.json"], names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Built_in_set_equals_the_recorded_table()
    {
        var lines = LibraryModels.BuiltIn.Members.Select(member =>
                $"M {member.Id} | {string.Join(',', member.Effects.Select(effect => Effect(effect.Kind) + ":" + effect.Parameter))} | " +
                string.Join(';', member.Assemblies.Select(range => $"{range.AssemblyName}@{range.Minimum}-{range.MaximumExclusive}")) +
                Vocabulary(member))
            .Concat(LibraryModels.BuiltIn.ImmutableTypes.Select(type =>
                $"T {type.Id} | derived={type.IncludesDerived.ToString().ToLowerInvariant()} | " +
                string.Join(';', type.Assemblies.Select(range => $"{range.AssemblyName}@{range.Minimum}-{range.MaximumExclusive}"))))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var recorded = Path.GetFullPath("../../../Providers/BuiltInTable.txt", AppContext.BaseDirectory);
        Assert.Equal(File.ReadAllLines(recorded), lines);
    }

    /// <summary>The line's tail for a result, fates, stores and outputs: parameters in ordinal order, each delegate parameter's inputs
    /// in order with their values sorted, and absent inputs as one empty list per parameter of the delegate.</summary>
    /// <param name="member">The model whose vocabulary is printed.</param>
    private static string Vocabulary(LibraryModel member) =>
        (member.Result is { } result ? $" | result={result}" : "") +
        (member.Fates.Count == 0 ? "" : " | fates=" + string.Join(';', member.Fates.OrderBy(fate => fate.Parameter, StringComparer.Ordinal).Select(fate =>
            $"{fate.Parameter}:{LibraryFate.Text(fate.Kind)}" +
            (fate.Holder is { } holder ? "/" + (holder == LibraryHolderKind.Result ? "result" : "this") : "") +
            string.Concat((fate.Inputs ?? Enumerable.Repeat<IReadOnlyList<LibraryValue>>([], ModelVocabularyTests.DelegateArity(member.Id, fate.Parameter)))
                              .Select(input => "[" + string.Join(',', input.Select(value => value.ToString()).Order(StringComparer.Ordinal)) + "]"))))) +
        (member.Stores.Count == 0 ? "" : " | stores=" + string.Join(';', member.Stores.OrderBy(store => store.Key, StringComparer.Ordinal)
            .Select(store => store.Key + "=[" + string.Join(',', store.Value.Select(value => value.ToString()).Order(StringComparer.Ordinal)) + "]"))) +
        (member.Keeps.Count == 0 ? "" : " | keeps=" + string.Join(';', member.Keeps.OrderBy(keep => keep.Key, StringComparer.Ordinal)
            .Select(keep => keep.Key + "=[" + string.Join(',', keep.Value.Select(value => value.ToString()).Order(StringComparer.Ordinal)) + "]"))) +
        (member.Outputs.Count == 0 ? "" : " | outputs=" + string.Join(';', member.Outputs.OrderBy(output => output.Key, StringComparer.Ordinal)
            .Select(output => output.Key + "=" + output.Value)));

    [Fact]
    public void Parameter_with_two_effect_kinds_keeps_both_in_order()
    {
        var member = Assert.Single(LibraryModels.BuiltIn.Members, entry => entry.Id ==
            "M:Microsoft.EntityFrameworkCore.DbContext.Add(System.Object)~Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry");
        Assert.Equal([LibraryEffect.DeepReadOf("entity"), LibraryEffect.WriteOf("entity")], member.Effects);
    }

    [Fact]
    public void Entry_assemblies_override_the_file_default()
    {
        var file = Valid.Replace("\"member\":\"M:A.B.Run\"", "\"member\":\"M:A.B.Run\",\"assemblies\":[\"B\"]");
        var member = Assert.Single(BuiltInModelReader.Read(Encoding.UTF8.GetBytes(file)).Members);
        Assert.Equal("B", Assert.Single(member.Assemblies).AssemblyName);
    }

    [Fact]
    public void Notes_are_ignored()
    {
        var file = Valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"note\":\"file\"")
                        .Replace("\"effects\":{}", "\"effects\":{},\"note\":\"entry\"")
                        .Replace("\"type\":\"T:A.B\"", "\"type\":\"T:A.B\",\"note\":\"type\"");
        Assert.Single(BuiltInModelReader.Read(Encoding.UTF8.GetBytes(file)).Members);
    }

    [Fact]
    public void Built_in_file_with_a_bom_loads()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Valid)).ToArray();
        Assert.Single(BuiltInModelReader.Read(bytes).Members);
    }

    public static IEnumerable<object[]> InvalidFiles()
    {
        var cases = new[]
        {
            Valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"extra\":1"),
            Valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
            Valid.Replace("\"effects\":{}", "\"effects\":{\"value\":[\"other\"]}"),
            Valid.Replace("\"effects\":{}", "\"effects\":{\"value\":[]}"),
            Valid.Replace("\"effects\":{}", "\"effects\":{\"value\":[\"reads-deep\",\"reads-deep\"]}"),
            Valid.Replace("\"versions\":{\"minimum\":\"1.0.0.0\",\"maximumExclusive\":\"2.0.0.0\"},", ""),
            Valid.Replace("\"assemblies\":[\"A\"],", ""),
            Valid.Replace("\"effects\":{}", "" ).Replace("\"member\":\"M:A.B.Run\",", "\"member\":\"M:A.B.Run\""),
            Valid.Replace("\"effects\":{}", "\"opaque\":true"),
            Valid.Replace("M:A.B.Run", "M:A.B.Run(*)"),
            Valid.Replace("M:A.B.Run", "M:A.B.Run~"),
            Valid.Replace("M:A.B.Run", "M:A.B.Run((System.Int32)"),
            Valid.Replace("M:A.B.Run", "M:A.B.Run(System.Int32,)"),
            // These two lead with the property they change, so that their rows are named apart from the others'.
            """{"models":[{"member":"M:A.B.Run!","effects":{}}],"schemaVersion":1,"assemblies":["A"],"versions":{"minimum":"1.0.0.0","maximumExclusive":"2.0.0.0"},"immutableTypes":[{"type":"T:A.B"}]}""",
            """{"immutableTypes":[{"type":"T:A.B-C"}],"schemaVersion":1,"assemblies":["A"],"versions":{"minimum":"1.0.0.0","maximumExclusive":"2.0.0.0"},"models":[{"member":"M:A.B.Run","effects":{}}]}""",
            Valid.Replace("\"minimum\":\"1.0.0.0\"", "\"minimum\":\"2.0.0.0\""),
            Valid.Replace("1.0.0.0", "1.0"),
            Valid.Replace("1.0.0.0", "1.0.0"),
            Valid.Replace("\"type\":\"T:A.B\"", "\"note\":\"missing type\""),
            Valid.Replace("T:A.B", "A.B"),
            Valid.Replace("T:A.B", "T:System.Bad Name"),
            Valid.Replace("\"type\":\"T:A.B\"", "\"type\":\"T:A.B\",\"extra\":1"),
            Valid.Replace("\"type\":\"T:A.B\"", "\"type\":\"T:A.B\",\"includesDerived\":\"yes\""),
            Valid.Replace("\"assemblies\":[\"A\"],", "").Replace("\"effects\":{}", "\"effects\":{},\"assemblies\":[\"A\"]"),
            Valid.Replace("\"versions\":{\"minimum\":\"1.0.0.0\",\"maximumExclusive\":\"2.0.0.0\"},", "")
                 .Replace("\"effects\":{}", "\"effects\":{},\"versions\":{\"minimum\":\"1.0.0.0\",\"maximumExclusive\":\"2.0.0.0\"}"),
            Valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,"),
            Valid.Replace("\"effects\":{}", "\"effects\":{},\"effects\":{}")
        };
        foreach (var file in cases)
            yield return [Encoding.UTF8.GetBytes(file)];
        yield return [new byte[] { 0xFF }];
    }

    private static string Effect(LibraryEffectKind kind) => kind switch
    {
        LibraryEffectKind.DeepRead => "reads-deep",
        LibraryEffectKind.WriteArgument => "writes-arg",
        LibraryEffectKind.WriteCells => "writes-cells",
        _ => throw new System.Diagnostics.UnreachableException($"Unknown effect kind {kind}.")
    };

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Refused_built_in_files(byte[] bytes) => Assert.Throws<LibraryModelException>(() => BuiltInModelReader.Read(bytes));
}
