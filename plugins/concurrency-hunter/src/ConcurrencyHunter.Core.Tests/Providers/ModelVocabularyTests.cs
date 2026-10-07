using System.Text;
using System.Text.Json;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class ModelVocabularyTests
{
    private const string BUILT_IN = """
        {"schemaVersion":1,"assemblies":["A"],"versions":{"minimum":"1.0.0.0","maximumExclusive":"2.0.0.0"},"models":[{"member":"M:A.B.Run","effects":{}ENTRY}]}
        """;

    private const string VALID = ""","result":"sequence(returns:f)","fates":{"f":{"fate":"iterator","inputs":[["elements(arg:source)"]],"note":"n"},"g":{"fate":"invoke-now","inputs":[["returns:g"],[]]}}""";

    /// <summary>A compilation over the metadata references the engine fixture compiles against.</summary>
    private static readonly Lazy<Compilation> Fixture = new(() => CSharpCompilation.Create(
        "Models", [],
        StubAssemblies.PlatformWithout([]).Concat(StubAssemblies.Names.Select(name => StubAssemblies.Get(name, StubAssemblies.DefaultVersion(name))))
                                         .Append(MetadataReference.CreateFromFile(typeof(Newtonsoft.Json.JsonConvert).Assembly.Location)),
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

    /// <summary>How many parameters the <c>Invoke</c> of a built-in member's delegate parameter takes.</summary>
    /// <param name="memberId">The member's documentation comment id.</param>
    /// <param name="parameter">The name of the member's delegate parameter.</param>
    internal static int DelegateArity(string memberId, string parameter) =>
        ((INamedTypeSymbol)Resolve(memberId).First().Parameters.Single(candidate => candidate.Name == parameter).Type).DelegateInvokeMethod!.Parameters.Length;

    private static IMethodSymbol[] Resolve(string memberId) =>
        DocumentationCommentId.GetSymbolsForDeclarationId(memberId, Fixture.Value).OfType<IMethodSymbol>().ToArray();

    [Fact]
    public void Every_built_in_fate_and_result_fits_its_member()
    {
        var described = LibraryModels.BuiltIn.Members.Where(member => member.Fates.Count != 0 || member.Result is not null ||
            member.Effects.Any(effect => effect.Kind == LibraryEffectKind.WriteCells) || member.Stores.Count != 0 || member.Outputs.Count != 0 || member.Keeps.Count != 0).ToArray();

        Assert.All(described, member =>
        {
            var methods = Resolve(member.Id);
            Assert.NotEmpty(methods);
            Assert.All(methods, method => Assert.Null(LibraryVocabulary.Member(member.Result, member.Fates, method, Fixture.Value,
                member.Effects, member.Stores, member.Outputs, member.Keeps)));
        });
        Assert.Equal(CarriedByTheFiles(), described.Length);
    }

    [Fact]
    public void Built_in_entry_with_fates_and_a_result_loads()
    {
        var member = Assert.Single(BuiltInModelReader.Read(Encoding.UTF8.GetBytes(BUILT_IN.Replace("ENTRY", VALID))).Members);

        Assert.Equal("sequence(returns:f)", member.Result?.ToString());
        Assert.Equal(["f", "g"], member.Fates.Select(fate => fate.Parameter).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Built_in_entries_look_through_task_results()
    {
        var iterator = Assert.Single(BuiltInModelReader.Read(Encoding.UTF8.GetBytes(BUILT_IN.Replace(
            "ENTRY", ""","result":"task(sequence(returns:f))","fates":{"f":{"fate":"iterator"}}"""))).Members);
        var keeper = Assert.Single(BuiltInModelReader.Read(Encoding.UTF8.GetBytes(BUILT_IN.Replace(
            "ENTRY", ""","result":"task(task(new))","keeps":{"result":["arg:a"]}"""))).Members);

        Assert.Equal("task(sequence(returns:f))", iterator.Result?.ToString());
        Assert.Equal((LibraryResultKind.Sequence, 1), (iterator.Result!.Leaf.Kind, iterator.Result.TaskDepth));
        Assert.Equal((LibraryResultKind.New, 2), (keeper.Result!.Leaf.Kind, keeper.Result.TaskDepth));
    }

    [Theory]
    [InlineData("arg:source", false)]
    [InlineData("returns:selector", false)]
    [InlineData("holder-arg:0", false)]
    [InlineData("holder-arg:12", false)]
    [InlineData("elements(arg:source)", false)]
    [InlineData("elements(returns:collectionSelector)", false)]
    [InlineData("sequence(arg:a)", false)]
    [InlineData("sequence(elements(arg:first),elements(arg:second))", false)]
    [InlineData("grouping(returns:keySelector,elements(arg:source))", false)]
    [InlineData("sequence(grouping(returns:k,sequence(returns:e)))", false)]
    [InlineData("arg:_value2", false)]
    [InlineData("this", false)]
    [InlineData("elements(this)", false)]
    [InlineData("[this]", true)]
    [InlineData("sequence(returns:selector)", true)]
    [InlineData("collection(elements(arg:source))", true)]
    [InlineData("collection(arg:a,returns:b)", true)]
    [InlineData("dictionary(returns:keySelector,elements(arg:source))", true)]
    [InlineData("[elements(arg:source)]", true)]
    [InlineData("[elements(arg:source),arg:defaultValue]", true)]
    [InlineData("sequence(grouping(returns:keySelector,elements(arg:source)))", true)]
    [InlineData("completion(arg:task)", false)]
    [InlineData("completion(completion(arg:nested))", false)]
    [InlineData("elements(completion(arg:task))", false)]
    [InlineData("task(new)", true)]
    [InlineData("task(task(new))", true)]
    [InlineData("task(sequence(returns:selector))", true)]
    [InlineData("task([completion(arg:task),arg:other])", true)]
    [InlineData("task(collection(elements(completion(arg:task))))", true)]
    public void Accepted_values(string text, bool result) =>
        Assert.Equal(text, result ? LibraryResult.Parse(text)?.ToString() : LibraryValue.Parse(text)?.ToString());

    [Theory]
    [InlineData("arg: source", false)]
    [InlineData("this:x", false)]
    [InlineData("this", true)]
    [InlineData("[this,]", true)]
    [InlineData(" arg:source", false)]
    [InlineData("argument:source", false)]
    [InlineData("arg:", false)]
    [InlineData("arg:1source", false)]
    [InlineData("arg:a-b", false)]
    [InlineData("holder-arg:01", false)]
    [InlineData("holder-arg:", false)]
    [InlineData("holder-arg:x", false)]
    [InlineData("elements(arg:source", false)]
    [InlineData("elements(arg:source))", false)]
    [InlineData("grouping(arg:a)", false)]
    [InlineData("grouping(arg:a,arg:b,arg:c)", false)]
    [InlineData("sequence()", false)]
    [InlineData("collection(arg:a)", false)]
    [InlineData("dictionary(arg:a,arg:b)", false)]
    [InlineData("[arg:a]", false)]
    [InlineData("sequence(arg:a,)", false)]
    [InlineData("[arg:a,]", true)]
    [InlineData("[]", true)]
    [InlineData("dictionary(arg:a)", true)]
    [InlineData("collection()", true)]
    [InlineData("grouping(arg:a,arg:b)", true)]
    [InlineData("arg:a", true)]
    [InlineData("[arg:a] ", true)]
    [InlineData("", false)]
    [InlineData("completion()", false)]
    [InlineData("completion(arg:task", false)]
    [InlineData("task(new)", false)]
    [InlineData("completion(arg:task)", true)]
    [InlineData("task()", true)]
    [InlineData("task(new", true)]
    [InlineData("task(arg:a)", true)]
    [InlineData("task(new,new)", true)]
    public void Refused_values(string text, bool result) =>
        Assert.Null(result ? LibraryResult.Parse(text) : LibraryValue.Parse(text));

    [Theory]
    [InlineData("repeated result", ""","result":"[arg:a]","result":"[arg:a]" """, "Repeated JSON property 'result'")]
    [InlineData("repeated fates", ""","fates":{},"fates":{}""", "Repeated JSON property 'fates'")]
    [InlineData("parameter named twice", ""","fates":{"f":{"fate":"invoke-now"},"f":{"fate":"invoke-now"}}""", "Repeated JSON property 'f'")]
    [InlineData("fates not an object", ""","fates":[]""", "Invalid built-in library model")]
    [InlineData("fate not an object", ""","fates":{"f":"invoke-now"}""", "Invalid built-in library model")]
    [InlineData("no fate", ""","fates":{"f":{}}""", "needs a fate")]
    [InlineData("unknown fate", ""","fates":{"f":{"fate":"later"}}""", "unknown fate 'later'")]
    [InlineData("di-factory", ""","fates":{"f":{"fate":"di-factory"}}""", "phase 5d")]
    [InlineData("holder without holder", ""","fates":{"f":{"fate":"holder"}}""", "needs its holder")]
    [InlineData("holder neither result nor this", ""","fates":{"f":{"fate":"holder","holder":"field"}}""", "holder must be result or this")]
    [InlineData("holder on another fate", ""","fates":{"f":{"fate":"invoke-now","holder":"result"}}""", "holder goes only with the holder fate")]
    [InlineData("inputs not an array", ""","fates":{"f":{"fate":"invoke-now","inputs":"arg:a"}}""", "Invalid built-in library model")]
    [InlineData("input not an array", ""","fates":{"f":{"fate":"invoke-now","inputs":["arg:a"]}}""", "Invalid built-in library model")]
    [InlineData("value not a string", ""","fates":{"f":{"fate":"invoke-now","inputs":[[1]]}}""", "Invalid built-in library model")]
    [InlineData("value that does not parse", ""","fates":{"f":{"fate":"invoke-now","inputs":[["arg:"]]}}""", "input 'arg:' does not parse")]
    [InlineData("holder-arg outside a holder", ""","fates":{"f":{"fate":"invoke-now","inputs":[["holder-arg:0"]]}}""", "stands only alone")]
    [InlineData("elements of holder-arg", ""","fates":{"f":{"fate":"holder","holder":"this","inputs":[["elements(holder-arg:0)"]]}}""", "stands only alone")]
    [InlineData("sequence of holder-arg", ""","fates":{"f":{"fate":"holder","holder":"this","inputs":[["sequence(holder-arg:0)"]]}}""", "stands only alone")]
    [InlineData("grouping of holder-arg", ""","fates":{"f":{"fate":"holder","holder":"this","inputs":[["grouping(holder-arg:0,arg:x)"]]}}""", "stands only alone")]
    [InlineData("holder-arg in a result", ""","result":"[holder-arg:0]" """, "stands only alone")]
    [InlineData("returns naming no fate", ""","result":"[returns:g]","fates":{"f":{"fate":"invoke-now"}}""", "names no fate")]
    [InlineData("returns of a holder", ""","fates":{"f":{"fate":"holder","holder":"this"},"g":{"fate":"invoke-now","inputs":[["returns:f"]]}}""", "holder delegate")]
    [InlineData("returns of a startup delegate", ""","result":"[returns:f]","fates":{"f":{"fate":"startup"}}""", "startup delegate")]
    [InlineData("returns of an unknown-execution delegate", ""","result":"[returns:f]","fates":{"f":{"fate":"unknown-execution"}}""", "unknown-execution delegate")]
    [InlineData("returns of an iterator in a collection", ""","result":"collection(returns:f)","fates":{"f":{"fate":"iterator"}}""", "iterator delegate")]
    [InlineData("returns of an iterator in an invoke-now input", ""","result":"sequence(arg:a)","fates":{"f":{"fate":"iterator"},"g":{"fate":"invoke-now","inputs":[["returns:f"]]}}""", "iterator delegate")]
    [InlineData("iterator without a sequence", ""","result":"[arg:a]","fates":{"f":{"fate":"iterator"}}""", "needs a sequence")]
    [InlineData("holder of the result beside a result", ""","result":"[arg:a]","fates":{"f":{"fate":"holder","holder":"result"}}""", "carries no result")]
    [InlineData("iterator under a task without a sequence", ""","result":"task([arg:a])","fates":{"f":{"fate":"iterator"}}""", "needs a sequence")]
    [InlineData("holder of the result beside a task result", ""","result":"task(new)","fates":{"f":{"fate":"holder","holder":"result"}}""", "carries no result")]
    [InlineData("result keeper beside a task of no new", ""","result":"task([arg:a])","keeps":{"result":["arg:b"]}""", "keeps.result needs the result new")]
    [InlineData("task in outputs", ""","outputs":{"o":"task(new)"}""", "outputs never take task")]
    [InlineData("new inside completion", ""","result":"[completion(new)]" """, "new is not allowed")]
    [InlineData("completion of a sequence", ""","result":"[completion(sequence(arg:a))]" """, "needs a task")]
    [InlineData("completion of a grouping", ""","result":"[completion(grouping(arg:a,arg:b))]" """, "needs a task")]
    [InlineData("result not a string", ""","result":1""", "Invalid built-in library model")]
    [InlineData("result that does not parse", ""","result":"list(arg:a)" """, "does not parse")]
    [InlineData("repeated fate property", ""","fates":{"f":{"fate":"invoke-now","fate":"invoke-now"}}""", "Repeated JSON property 'fate'")]
    [InlineData("unknown fate property", ""","fates":{"f":{"fate":"invoke-now","runs":"now"}}""", "Invalid built-in library model")]
    [InlineData("fate key that is no name", ""","fates":{"action!":{"fate":"invoke-now"}}""", "keyed by parameter names")]
    public void Refused_built_in_fate_entries(string name, string entry, string reason)
    {
        Assert.NotEmpty(name);
        var error = Assert.Throws<LibraryModelException>(() => BuiltInModelReader.Read(Encoding.UTF8.GetBytes(BUILT_IN.Replace("ENTRY", entry.TrimEnd()))));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    /// <summary>The entries of the seven embedded files that carry a result or a fate.</summary>
    private static int CarriedByTheFiles()
    {
        var assembly = typeof(LibraryModels).Assembly;
        var count = 0;
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("LibraryModels.BuiltIn.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var document = JsonDocument.Parse(stream);
            count += document.RootElement.GetProperty("models").EnumerateArray().Count(entry =>
                entry.TryGetProperty("result", out _) ||
                entry.TryGetProperty("fates", out var fates) && fates.EnumerateObject().Any() ||
                entry.TryGetProperty("stores", out _) || entry.TryGetProperty("outputs", out _) || entry.TryGetProperty("keeps", out _) ||
                entry.GetProperty("effects").EnumerateObject().Any(effect => effect.Value.EnumerateArray().Any(kind => kind.GetString() == "writes-cells")));
        }
        return count;
    }
}
