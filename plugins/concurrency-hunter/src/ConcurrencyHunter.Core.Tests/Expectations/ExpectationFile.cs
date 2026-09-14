using System.Text.Json;

namespace ConcurrencyHunter.Core.Tests.Expectations;

internal sealed record ExpectationFile(string SchemaVersion, IReadOnlyList<FindingExpectation> Findings,
                                       IReadOnlyList<NotDefectExpectation> NotDefects)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static ExpectationFile Load(string path) => Parse(File.ReadAllText(path));

    internal static ExpectationFile Parse(string json) =>
        JsonSerializer.Deserialize<ExpectationFile>(json, Options) ??
        throw new InvalidOperationException("The expectation file was empty.");
}

internal sealed record FindingExpectation(string Id, string Phase, string Rule, string Confidence,
                                          ExpectationResource Resource,
                                          IReadOnlyList<ExpectationAccess> Accesses);

internal sealed record NotDefectExpectation(string Id, string Phase, ExpectationResource Resource,
                                            IReadOnlyList<ExpectationAccess>? Accesses = null);

internal sealed record ExpectationResource(string Region, IReadOnlyList<string> AccessPath);

internal sealed record ExpectationAccess(string Symbol, string Operation);
