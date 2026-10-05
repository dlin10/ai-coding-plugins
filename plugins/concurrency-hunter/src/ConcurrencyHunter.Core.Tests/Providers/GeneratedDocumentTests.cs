using System.Text;
using System.Text.Json;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>The generator's answer as <c>generate</c> writes it (G-6).</summary>
public sealed class GeneratedDocumentTests
{
    private const string ALL = "M:System.Linq.Enumerable.All``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})";
    private const string JOIN = "M:System.String.Join(System.String,System.String[])";
    private const string MVID = "5c8d0b6e-7a4f-4b1e-9f2a-3d6c1e8b0a47";

    private static readonly string[] TOP_LEVEL = ["schemaVersion", "member", "assembly", "classified", "reason", "model", "modelReason", "generation"];

    private static readonly string[] GENERATION =
    [
        "implementation", "framework", "missingDependencies", "externBodies", "defaultedValues", "refusals", "setupWidened", "seeds", "unseeded",
        "holderTriggers", "reachedBodies", "seconds"
    ];

    // G-6's two examples, their "…" filled.
    private const string FIRST_EXAMPLE =
        "{\"schemaVersion\":2,\"member\":\"" + ALL + "\",\"assembly\":{\"name\":\"System.Linq\",\"version\":\"10.0\",\"package\":null}," +
        "\"classified\":{\"predicate\":{\"fate\":\"invoke-now\",\"holder\":null}},\"reason\":null,\"model\":null,\"modelReason\":null," +
        "\"generation\":{\"implementation\":" +
        "{\"path\":\"C:\\\\Program Files\\\\dotnet\\\\shared\\\\Microsoft.NETCore.App\\\\10.0.0\\\\System.Linq.dll\",\"assemblyVersion\":\"10.0.0.0\"," +
        "\"fileVersion\":\"10.0.25.52411\",\"mvid\":\"" + MVID + "\"},\"framework\":\"net10.0\",\"missingDependencies\":[],\"externBodies\":3," +
        "\"defaultedValues\":[],\"refusals\":{},\"setupWidened\":[],\"seeds\":0,\"unseeded\":[],\"holderTriggers\":{}," +
        "\"reachedBodies\":41,\"seconds\":2.3}}\n";

    private const string SECOND_EXAMPLE =
        "{\"schemaVersion\":2,\"member\":\"" + JOIN + "\",\"assembly\":{\"name\":\"System.Private.CoreLib\",\"version\":\"8.0\",\"package\":null}," +
        "\"classified\":null,\"reason\":\"corelib\",\"model\":null,\"modelReason\":null,\"generation\":{\"implementation\":null," +
        "\"framework\":null,\"missingDependencies\":[],\"externBodies\":0,\"defaultedValues\":[],\"refusals\":{},\"setupWidened\":[]," +
        "\"seeds\":0,\"unseeded\":[],\"holderTriggers\":{},\"reachedBodies\":0,\"seconds\":0.0}}\n";

    [Fact]
    public void Classified_answer_has_every_property_typed_as_G6_says()
    {
        using var document = JsonDocument.Parse(GeneratedDocument.Write(Rich()));
        var root = document.RootElement;

        Assert.Equal(TOP_LEVEL, Names(root));
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.String, root.GetProperty("member").ValueKind);
        var assembly = root.GetProperty("assembly");
        Assert.Equal(["name", "version", "package"], Names(assembly));
        Assert.Equal("Polly", assembly.GetProperty("package").GetString());
        var classified = root.GetProperty("classified");
        Assert.Equal(["onRetry", "sleepDurationProvider"], Names(classified));
        Assert.All(classified.EnumerateObject(), parameter => Assert.Equal(["fate", "holder"], Names(parameter.Value)));
        Assert.Equal("result", classified.GetProperty("sleepDurationProvider").GetProperty("holder").GetString());
        Assert.Equal(JsonValueKind.Null, classified.GetProperty("onRetry").GetProperty("holder").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reason").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("model").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("modelReason").ValueKind);

        var generation = root.GetProperty("generation");
        Assert.Equal(GENERATION, Names(generation));
        Assert.Equal(["path", "assemblyVersion", "fileVersion", "mvid"], Names(generation.GetProperty("implementation")));
        Assert.All(generation.GetProperty("implementation").EnumerateObject(), property => Assert.Equal(JsonValueKind.String, property.Value.ValueKind));
        Assert.Equal("net8.0", generation.GetProperty("framework").GetString());
        Assert.Equal(["Polly.Extensions@[1.0.0]"], Strings(generation.GetProperty("missingDependencies")));
        Assert.Equal(7, generation.GetProperty("externBodies").GetInt32());
        Assert.Equal(["context"], Strings(generation.GetProperty("defaultedValues")));
        Assert.Equal("holder this needs an instance method", generation.GetProperty("refusals").GetProperty("onRetry").GetString());
        Assert.Equal(["onRetry"], Strings(generation.GetProperty("setupWidened")));
        Assert.Equal(3, generation.GetProperty("seeds").GetInt32());
        Assert.Equal(["arg:context/F:Lib.Context.Value"], Strings(generation.GetProperty("unseeded")));
        Assert.Equal(["onRetry"], Names(generation.GetProperty("holderTriggers")));
        Assert.Equal(["M:Polly.Result.Fire"], Strings(generation.GetProperty("holderTriggers").GetProperty("onRetry")));
        Assert.Equal(152, generation.GetProperty("reachedBodies").GetInt32());
        Assert.Equal(12.0, generation.GetProperty("seconds").GetDouble());
    }

    [Fact]
    public void Answer_with_a_reason_has_every_property_typed_as_G6_says()
    {
        var answer = Example(JOIN, new GenerationAssembly("Polly", "7.2.3", "Polly"), null, GenerationReasons.CLOSURE_BOUND,
                             Record(new GenerationImplementation("p", "7.0.0.0", "7.2.3.0", MVID), "net8.0", reachedBodies: 1501, seconds: 1.04));
        using var document = JsonDocument.Parse(GeneratedDocument.Write(answer));
        var root = document.RootElement;

        Assert.Equal(TOP_LEVEL, Names(root));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("classified").ValueKind);
        Assert.Equal("closure-bound", root.GetProperty("reason").GetString());
        var generation = root.GetProperty("generation");
        Assert.Equal(GENERATION, Names(generation));
        Assert.Equal(JsonValueKind.Object, generation.GetProperty("implementation").ValueKind);
        foreach (var name in new[] { "missingDependencies", "defaultedValues", "setupWidened", "unseeded" })
            Assert.Equal(JsonValueKind.Array, generation.GetProperty(name).ValueKind);
        foreach (var name in new[] { "refusals", "holderTriggers" })
            Assert.Equal(JsonValueKind.Object, generation.GetProperty(name).ValueKind);
        foreach (var name in new[] { "externBodies", "seeds", "reachedBodies", "seconds" })
            Assert.Equal(JsonValueKind.Number, generation.GetProperty(name).ValueKind);
        Assert.Equal(1501, generation.GetProperty("reachedBodies").GetInt32());
        Assert.Equal(1.0, generation.GetProperty("seconds").GetDouble());
    }

    [Fact]
    public void First_example_is_written_byte_for_byte()
    {
        var answer = Example(ALL, new GenerationAssembly("System.Linq", "10.0", null),
                             new SortedDictionary<string, ClassifiedFate>(StringComparer.Ordinal) { ["predicate"] = new("invoke-now", null) }, null,
                             Record(new GenerationImplementation(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.0\System.Linq.dll", "10.0.0.0",
                                                                 "10.0.25.52411", MVID),
                                    "net10.0", externBodies: 3, reachedBodies: 41, seconds: 2.3));

        Assert.Equal(FIRST_EXAMPLE, Encoding.UTF8.GetString(GeneratedDocument.Write(answer)));
    }

    [Fact]
    public void Second_example_is_written_byte_for_byte()
    {
        var answer = Example(JOIN, new GenerationAssembly("System.Private.CoreLib", "8.0", null), null, GenerationReasons.CORELIB,
                             Record(null, null, seconds: 0.04));

        Assert.Equal(Encoding.UTF8.GetBytes(SECOND_EXAMPLE), GeneratedDocument.Write(answer));
    }

    [Fact]
    public void Document_is_utf8_without_bom_with_lf_and_one_final_newline()
    {
        var bytes = GeneratedDocument.Write(Rich());

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal((byte)'{', bytes[0]);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^2]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Single(bytes, (byte)'\n');
        Assert.Equal(bytes, Encoding.UTF8.GetBytes(new UTF8Encoding(false, true).GetString(bytes)));
    }

    [Theory]
    [InlineData(0.0, "0.0")]
    [InlineData(0.04, "0.0")]
    [InlineData(2.26, "2.3")]
    [InlineData(12.0, "12.0")]
    [InlineData(371.96, "372.0")]
    public void Seconds_are_written_with_one_decimal(double seconds, string written)
    {
        var text = Encoding.UTF8.GetString(GeneratedDocument.Write(Example(JOIN, new GenerationAssembly("A", "8", null), null, GenerationReasons.CORELIB,
                                                                           Record(null, null, seconds: seconds))));

        Assert.EndsWith($"\"seconds\":{written}}}}}\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Ids_and_paths_keep_their_characters_but_json_escapes()
    {
        var member = "M:Acme.Map`2.Get``1(Acme.Map{`0,`1}@,``0[0:,0:])";
        var answer = Example(member, new GenerationAssembly("Acme", "1.0.0-beta+meta", "Acme"), null, GenerationReasons.MEMBER_NOT_FOUND,
                             Record(new GenerationImplementation("C:\\pkgs\\acme\\\"q\"\\Acme.dll", "1.0.0.0", "1.0.0.0", MVID), "net8.0"));
        var text = Encoding.UTF8.GetString(GeneratedDocument.Write(answer));

        Assert.Contains($"\"member\":\"{member}\"", text, StringComparison.Ordinal);
        Assert.Contains("\"version\":\"1.0.0-beta+meta\"", text, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"C:\\\\pkgs\\\\acme\\\\\\\"q\\\"\\\\Acme.dll\"", text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(member, document.RootElement.GetProperty("member").GetString());
    }

    private static GeneratedAnswer Rich() =>
        Example("M:Polly.RetrySyntax.WaitAndRetry(Polly.PolicyBuilder,System.Int32,System.Func{System.Int32,System.TimeSpan},System.Action{System.Exception,System.TimeSpan})",
                new GenerationAssembly("Polly", "7.2.3", "Polly"),
                new SortedDictionary<string, ClassifiedFate>(StringComparer.Ordinal)
                {
                    ["onRetry"] = new("unknown-execution", null),
                    ["sleepDurationProvider"] = new("holder", "result")
                },
                null,
                new GenerationRecord(new GenerationImplementation(@"C:\packages\polly\7.2.3\lib\netstandard2.0\Polly.dll", "7.0.0.0", "7.2.3.0", MVID), "net8.0",
                                     ["Polly.Extensions@[1.0.0]"], 7, ["context"],
                                     new SortedDictionary<string, string>(StringComparer.Ordinal) { ["onRetry"] = "holder this needs an instance method" },
                                     ["onRetry"], 3, ["arg:context/F:Lib.Context.Value"],
                                     new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                                     {
                                         ["onRetry"] = ["M:Polly.Result.Fire"]
                                     }, 152, 12.0));

    private static GeneratedAnswer Example(string member, GenerationAssembly assembly, IReadOnlyDictionary<string, ClassifiedFate>? classified, string? reason,
                                           GenerationRecord record) =>
        new(2, member, assembly, classified, reason, null, null, record);

    private static GenerationRecord Record(GenerationImplementation? implementation, string? framework, int externBodies = 0, int reachedBodies = 0,
                                           double seconds = 0) =>
        new(implementation, framework, [], externBodies, [], new SortedDictionary<string, string>(StringComparer.Ordinal), [], 0, [],
            new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), reachedBodies, seconds);

    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(property => property.Name).ToArray();

    private static string[] Strings(JsonElement element) => element.EnumerateArray().Select(item => item.GetString()!).ToArray();
}
