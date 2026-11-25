using System.Text.RegularExpressions;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>A probe fixture of a race form (issue 506): one singleton service shared by two hosted services, each running one
/// statement against it, analysed through the whole pipeline (<c>PhaseOneAnalyzer.AnalyzeAsync</c>: folded pairing, the solver and the
/// findings), so that its answer is the analyzer's end to end rather than the pairs before the solver. The two services start once
/// each and overlap each other, never themselves, so a pair of two runs of one statement is one service against the other. A site the
/// probe needs to tell apart from another in the same member carries a marker comment <c>/*@name*/</c> right before it, and an access
/// whose source span holds the marker is written with <c>@name</c>.</summary>
/// <param name="Service">The singleton type both hosted services take, declared in <paramref name="Types"/>.</param>
/// <param name="Types">The types of the case: the service and what it uses.</param>
/// <param name="First">The statement the first hosted service runs on the service, held in a field named after it.</param>
/// <param name="Second">The statement the second hosted service runs on it.</param>
/// <param name="Registrations">Registrations added before the service's own and the hosted services'.</param>
internal sealed partial record HostedServiceProbe(string Service, string Types, string First, string Second, string Registrations = "")
{
    /// <summary>Types every probe may use: a box with one value and a tally.</summary>
    private const string COMMON_TYPES = """
        public sealed class Box { public int Value; }
        public sealed class Tally { public int Value; }

        """;

    /// <summary>The usings every probe file starts with: the fixture's and the two collection namespaces.</summary>
    private const string PROBE_USINGS = "using System.Collections.Generic;\nusing System.Linq;\n";

    /// <summary>How <see cref="Describe"/> writes the holder of a resource that no field holds.</summary>
    public const string NO_FIELD = "(no field)";

    /// <summary>The field each hosted service keeps the service in: the service's name in camel case with a leading underscore.</summary>
    public string Field => "_" + char.ToLowerInvariant(Service[0]) + Service[1..];

    /// <summary>The source file of the probe.</summary>
    public string Source() =>
        COMMON_TYPES + Types + "\n" + Worker("First", First) + Worker("Second", Second) +
        Startup($"{Registrations} services.AddSingleton<{Service}>(); services.AddHostedService<FirstWorker>(); " +
                "services.AddHostedService<SecondWorker>();");

    /// <summary>The probe's findings as <see cref="Describe"/> writes them, after asserting that the source compiles.</summary>
    public async Task<string[]> FindingsAsync() => (await AnalyzeAsync(Source(), new ProbeInputs())).Findings;

    /// <summary>The probe's answer with further inputs of its project.</summary>
    /// <param name="inputs">Further files and stub assemblies of the project, and whether to mark uncertain findings.</param>
    public Task<ProbeAnswer> AnalyzeAsync(ProbeInputs inputs) => AnalyzeAsync(Source(), inputs);

    /// <summary>The findings of a whole source file, analysed as a probe is, as <see cref="Describe"/> writes them, after asserting
    /// that the source compiles: for a case whose roots are not two hosted services, which declares its own startup.</summary>
    /// <param name="source">The source file, without the fixture's usings.</param>
    public static async Task<string[]> FindingsAsync(string source) => (await AnalyzeAsync(source, new ProbeInputs())).Findings;

    /// <summary>The answer of a source file with further inputs of its project, analysed as a probe is, after asserting that the
    /// project compiles: its findings as <see cref="Describe"/> writes them, its accesses as <see cref="DescribeAccess"/> writes them
    /// and its roots.</summary>
    /// <param name="source">The main source file, without the fixture's usings.</param>
    /// <param name="inputs">Further files and stub assemblies of the project, and whether to mark uncertain findings.</param>
    public static async Task<ProbeAnswer> AnalyzeAsync(string source, ProbeInputs inputs)
    {
        var text = Usings + PROBE_USINGS + source;
        var options = new FixtureOptions
        {
            MetadataReferences = inputs.Stubs.Select(stub => StubAssemblies.Get(stub, FixtureOptions.DEFAULT_STUB_VERSION)).ToArray()
        };
        var solution = FixtureSolution.Create(options, [("Case.cs", text), .. inputs.Files]);
        var compilation = await solution.Projects.Single().GetCompilationAsync();
        Assert.Empty(compilation!.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var result = await PhaseOneAnalyzer.AnalyzeAsync(solution, ROOT_DIRECTORY, ProviderRegistry.BuiltIn, CancellationToken.None);
        var markers = Markers(text);
        return new ProbeAnswer(Describe(result.Findings, markers, inputs.MarkUncertain),
                               Canonical(result.Accesses.Where(access => !access.IsConstructionLocal).Select(access => DescribeAccess(access, markers))),
                               Canonical(result.Roots.Select(root => $"{root.RootKind} {Member(root.Entry.Symbol)}")))
        {
            Uncertainties = Canonical(result.Findings.SelectMany(finding => finding.Uncertainty)),
            UncertaintiesByFinding = result.Findings.GroupBy(finding => Describe([finding], markers, false)[0], StringComparer.Ordinal)
                                                    .ToDictionary(group => group.Key, group => Canonical(group.SelectMany(finding => finding.Uncertainty)),
                                                                  StringComparer.Ordinal)
        };
    }

    /// <summary>Asserts that a probe collected the accesses a claim names, and none on the resources it says were lost at their marked
    /// sites.</summary>
    /// <param name="answer">The probe's answer.</param>
    /// <param name="collected">Accesses that must be there, as <see cref="DescribeAccess"/> writes them.</param>
    /// <param name="lost">Resources at marked sites that must have no access, as <c>resource @marker</c>, <c>*</c> standing for any
    /// resource.</param>
    public static void AssertAccessClaims(ProbeAnswer answer, string[] collected, string[] lost)
    {
        Assert.All(collected, access => Assert.Contains(access, answer.Accesses));
        Assert.All(lost, site =>
        {
            var at = site.LastIndexOf(" @", StringComparison.Ordinal);
            var marker = site[(at + 1)..];
            var resource = site[..at];
            Assert.DoesNotContain(answer.Accesses, access => access.EndsWith(" " + marker, StringComparison.Ordinal) &&
                                                             (resource == "*" || access.Contains(" " + resource + " ", StringComparison.Ordinal)));
        });
    }

    /// <summary>Each finding as its rule, its protection result, its resource and its unordered pair of sides, distinct and in ordinal
    /// order: <c>rule protection resource: side | side</c>, the resource as <see cref="Resource"/> writes it, each side as
    /// <see cref="Side"/> writes it, the two in ordinal order; with <paramref name="markUncertain"/>, a finding with any uncertainty
    /// ends in <c>(uncertain)</c>.</summary>
    /// <param name="findings">The findings.</param>
    /// <param name="markers">The marked sites of the probe's file.</param>
    /// <param name="markUncertain">Whether to mark a finding that carries an uncertainty.</param>
    public static string[] Describe(IEnumerable<Finding> findings, IReadOnlyList<Marker> markers, bool markUncertain) =>
        Canonical(findings.Select(finding => $"{finding.RuleId} {finding.ProtectionResult} {Resource(finding.Resource)}: " +
                                             string.Join(" | ", new[] { Side(finding.AccessA, markers), Side(finding.AccessB, markers) }
                                                                .Order(StringComparer.Ordinal)) +
                                             (markUncertain && finding.Uncertainty.Count > 0 ? " (uncertain)" : string.Empty)));

    /// <summary>An access as its operation, its resource and its member with its marker: <c>operation resource member[ @marker]</c>.</summary>
    /// <param name="access">The access.</param>
    /// <param name="markers">The marked sites of the probe's file.</param>
    public static string DescribeAccess(Access access, IReadOnlyList<Marker> markers) =>
        $"{access.Operation} {Resource(access.Resource)} {Site(access, markers)}";

    /// <summary>A resource: the fields that hold it from its region, or <see cref="NO_FIELD"/> when none does, then the cell's
    /// selector after a dot where it is a cell, or <c>structure</c> after a space where it is a collection's structure rather than the
    /// field that holds it (ADR 0010).</summary>
    /// <param name="resource">The resource.</param>
    public static string Resource(AccessResource resource)
    {
        var fields = resource.AccessPath.Where(name => !name.StartsWith('[')).ToArray();
        var selectors = resource.AccessPath.Where(name => name.StartsWith('[')).ToArray();
        return (fields.Length == 0 ? NO_FIELD : string.Join(".", fields)) +
               string.Concat(selectors.Select(selector => "." + selector)) +
               (resource is { CollectionId: not null, Selector: null } ? " structure" : string.Empty);
    }

    /// <summary>A set of descriptions in its one canonical form: distinct and in ordinal order.</summary>
    /// <param name="descriptions">The descriptions.</param>
    public static string[] Canonical(IEnumerable<string> descriptions) =>
        descriptions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Asserts that a probe answered as run time calls for or, where it has a difference, as the difference pins, both read
    /// as sets.</summary>
    /// <param name="findings">The probe's findings as <see cref="Describe"/> writes them.</param>
    /// <param name="truth">The findings run time calls for.</param>
    /// <param name="difference">The probe's difference, or null.</param>
    public static void AssertAnswer(string[] findings, string[] truth, ProbeDifference? difference) =>
        Assert.Equal(Canonical(difference?.Answer ?? truth), Canonical(findings));

    /// <summary>Asserts that every difference names a probe, pins an answer whose set differs from the probe's truth and says why.</summary>
    /// <param name="truths">The truth of each probe by name.</param>
    /// <param name="differences">The differences by probe name.</param>
    public static void AssertDifferences(IReadOnlyDictionary<string, string[]> truths, IReadOnlyDictionary<string, ProbeDifference> differences) =>
        Assert.All(differences, difference =>
        {
            Assert.True(truths.ContainsKey(difference.Key), difference.Key);
            Assert.NotEqual(Canonical(truths[difference.Key]), Canonical(difference.Value.Answer));
            Assert.False(string.IsNullOrWhiteSpace(difference.Value.Reason));
        });

    /// <summary>The sites a text marks with <c>/*@name*/</c>, each at the position right after its comment.</summary>
    /// <param name="text">The text.</param>
    public static IReadOnlyList<Marker> Markers(string text)
    {
        var lines = text.Split('\n');
        var markers = new List<Marker>();
        for (var line = 0; line < lines.Length; line++)
            foreach (Match match in MarkerPattern().Matches(lines[line]))
                markers.Add(new Marker(match.Groups[1].Value, line + 1, match.Index + match.Length + 1));
        return markers;
    }

    /// <summary>One side of a finding: the access's operation and its site.</summary>
    /// <param name="access">The access.</param>
    /// <param name="markers">The marked sites of the probe's file.</param>
    private static string Side(Access access, IReadOnlyList<Marker> markers) => $"{access.Operation} {Site(access, markers)}";

    /// <summary>An access's site: its member without the parameter list (a lambda's accesses stand in the member that declares it),
    /// then <c>@name</c> of the first marker its source span holds, if any does.</summary>
    /// <param name="access">The access.</param>
    /// <param name="markers">The marked sites of the probe's file.</param>
    private static string Site(Access access, IReadOnlyList<Marker> markers)
    {
        var span = access.Source;
        var marker = markers.FirstOrDefault(marker =>
            (marker.Line > span.StartLine || marker.Line == span.StartLine && marker.Column >= span.StartColumn) &&
            (marker.Line < span.EndLine || marker.Line == span.EndLine && marker.Column <= span.EndColumn));
        return Member(access.Symbol) + (marker is null ? string.Empty : " @" + marker.Name);
    }

    /// <summary>A symbol without its parameter list.</summary>
    /// <param name="symbol">The symbol.</param>
    private static string Member(string symbol)
    {
        var parameters = symbol.IndexOf('(', StringComparison.Ordinal);
        return parameters < 0 ? symbol : symbol[..parameters];
    }

    [GeneratedRegex(@"/\*@([A-Za-z0-9-]+)\*/")]
    private static partial Regex MarkerPattern();

    /// <summary>A hosted service that takes the service and runs <paramref name="body"/> once.</summary>
    /// <param name="name">The prefix of the hosted service's name.</param>
    /// <param name="body">The statement it runs.</param>
    private string Worker(string name, string body) => $$"""

        public sealed class {{name}}Worker : BackgroundService
        {
            private readonly {{Service}} {{Field}};
            public {{name}}Worker({{Service}} service) => {{Field}} = service;
            protected override Task ExecuteAsync(CancellationToken stoppingToken)
            {
                {{body}}
                return Task.CompletedTask;
            }
        }
        """;
}

/// <summary>A site a probe marks: the marker's name and the 1-based line and column right after its comment.</summary>
/// <param name="Name">The marker's name.</param>
/// <param name="Line">The 1-based line.</param>
/// <param name="Column">The 1-based column right after the comment.</param>
internal sealed record Marker(string Name, int Line, int Column);

/// <summary>What the analysis answered on a probe, each part in its canonical form.</summary>
/// <param name="Findings">The findings, as <see cref="HostedServiceProbe.Describe"/> writes them.</param>
/// <param name="Accesses">The accesses that are not construction-local, as <see cref="HostedServiceProbe.DescribeAccess"/> writes them.</param>
/// <param name="Roots">The roots, each as its kind and its member without the parameter list.</param>
internal sealed record ProbeAnswer(string[] Findings, string[] Accesses, string[] Roots)
{
    /// <summary>Every line of uncertainty the findings carry, distinct and in ordinal order.</summary>
    public string[] Uncertainties { get; init; } = [];

    /// <summary>The lines of uncertainty of each finding, by the finding as <see cref="HostedServiceProbe.Describe"/> writes it without
    /// marks, distinct and in ordinal order.</summary>
    public IReadOnlyDictionary<string, string[]> UncertaintiesByFinding { get; init; } = new Dictionary<string, string[]>();
}

/// <summary>What a probe's project holds besides its main source file.</summary>
/// <param name="Files">Further files, each with its path and its whole text.</param>
/// <param name="Stubs">Stub assemblies outside <see cref="StubAssemblies.Names"/> the project references, by name.</param>
/// <param name="MarkUncertain">Whether <see cref="HostedServiceProbe.Describe"/> marks a finding that carries an uncertainty.</param>
internal sealed record ProbeInputs((string Path, string Source)[] Files, string[] Stubs, bool MarkUncertain)
{
    /// <summary>No further files, no further stubs, no marks.</summary>
    public ProbeInputs() : this([], [], false) { }
}

/// <summary>Where a probe's answer differs from what run time calls for: the answer the analysis gives, pinned, and why.</summary>
/// <param name="Answer">The findings the analysis gives, as <see cref="HostedServiceProbe.Describe"/> writes them.</param>
/// <param name="Reason">Why it differs: the issue of a wrong answer, or the construct the analysis does not model.</param>
internal sealed record ProbeDifference(string[] Answer, string Reason);
