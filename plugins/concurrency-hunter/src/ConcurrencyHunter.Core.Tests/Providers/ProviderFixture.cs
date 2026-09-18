using System.Text.RegularExpressions;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Roots;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>One provider contract case: sources, the framework they target, the assembly versions they reference,
/// and the expected result written by hand in <see cref="ProviderFixture"/>'s one-line notation.</summary>
public sealed record ProviderCase(string Name, IReadOnlyList<(string Path, string Source)> Sources)
{
    /// <summary><c>net&lt;major&gt;.0</c>: every framework stub assembly is referenced at that major version, the gRPC stubs at
    /// their own default.</summary>
    public string TargetFramework { get; init; } = "net10.0";

    /// <summary>Major version per assembly name, overriding the target framework. A name that is not a stub
    /// is referenced as an empty assembly with that identity.</summary>
    public IReadOnlyDictionary<string, int> AssemblyVersions { get; init; } = new Dictionary<string, int>();

    /// <summary>Stub assemblies the case does not reference at all.</summary>
    public IReadOnlyList<string> OmittedAssemblies { get; init; } = [];

    /// <summary>The multi-compilation form: sources by project, one compilation each, used instead of <see cref="Sources"/> when
    /// not empty.</summary>
    public IReadOnlyList<(string Project, string Path, string Source)> Projects { get; init; } = [];

    /// <summary>Major version per assembly name for one project of <see cref="Projects"/>, overriding the others.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> ProjectAssemblyVersions { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, int>>();

    /// <summary>Stub assemblies one project of <see cref="Projects"/> does not reference.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ProjectOmittedAssemblies { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    public RootDiscoveryStatus ExpectedStatus { get; init; } = RootDiscoveryStatus.Checked;
    public IReadOnlyList<string> ExpectedRoots { get; init; } = [];
    public IReadOnlyList<string> ExpectedDiagnostics { get; init; } = [];
}

/// <summary>
/// Runs a provider over a <see cref="ProviderCase"/> and compares its result with the case's expectations,
/// ignoring order. Notation, one line each:
/// <code>
/// root:       &lt;RootKind&gt; &lt;Symbol&gt; receiver=&lt;ReceiverKind&gt; parameters=[name:Kind,...] multiplicity=&lt;M&gt; overlap=&lt;O&gt; scope=&lt;ScopeBinding&gt;
///             [ completion=[Kind:EventRef,...]] [ ordering=[Before&lt;After@Guard,...]] [ flags=[flag,...]]
/// diagnostic: &lt;Code&gt; &lt;AffectedScope&gt;
/// </code>
/// Display text, source spans, evidence and diagnostic reasons are not part of the notation.
/// </summary>
public static partial class ProviderFixture
{
    public const string SCOPE_ID = "scope:Fixture";
    private const string ROOT_DIRECTORY = @"C:\fixture";

    public static RootDiscoveryResult Run(IExecutionRootProvider provider, ProviderCase testCase)
    {
        Assert.Matches(CaseName(), testCase.Name);
        var result = Discover(provider, testCase, "");

        Assert.Equal(testCase.ExpectedStatus, result.Status);
        Assert.Equal(Sorted(testCase.ExpectedRoots), Sorted(result.Roots.Select(Format)));
        Assert.Equal(Sorted(testCase.ExpectedDiagnostics), Sorted(result.Diagnostics.Select(Format)));
        Assert.All(result.Roots, root => Assert.Equal(provider.ProviderId, root.ProviderId));
        Assert.All(result.Diagnostics, diagnostic => Assert.Equal(provider.ProviderId, diagnostic.ProviderId));
        Assert.Equal(result.Roots.Count, result.Roots.Select(root => root.StableRootId).Distinct(StringComparer.Ordinal).Count());
        return result;
    }

    /// <summary>Runs the case as written and again with blank lines prepended to every source file; the stable
    /// root ids must not change.</summary>
    public static void AssertStableIds(IExecutionRootProvider provider, ProviderCase testCase)
    {
        var original = Discover(provider, testCase, "");
        var shifted = Discover(provider, testCase, "\n\n\n");

        Assert.NotEmpty(original.Roots);
        Assert.Equal(Sorted(original.Roots.Select(root => root.StableRootId)),
                     Sorted(shifted.Roots.Select(root => root.StableRootId)));
    }

    public static string Format(ExecutionRootDescriptor root)
    {
        var parameters = string.Join(",", root.InstanceBindings.Parameters.Select(parameter => $"{parameter.Name}:{parameter.Kind}"));
        var line = $"{root.RootKind} {root.Entry.Symbol} receiver={root.InstanceBindings.Receiver} parameters=[{parameters}] " +
                   $"multiplicity={root.InvocationPolicy.Multiplicity} overlap={root.InvocationPolicy.SelfOverlap} " +
                   $"scope={root.InvocationPolicy.ScopeBinding}";
        if (root.CompletionEvents.Count != 0)
            line += $" completion=[{string.Join(",", root.CompletionEvents.Select(completion => $"{completion.Kind}:{completion.EventRef}"))}]";
        if (root.OrderingConstraints.Count != 0)
        {
            line += $" ordering=[{string.Join(",", root.OrderingConstraints.Select(ordering =>
                $"{ordering.BeforeEventRef}<{ordering.AfterEventRef}" + (ordering.GuardRef is null ? "" : "@" + ordering.GuardRef)))}]";
        }
        if (root.PrecisionFlags.Count != 0)
            line += $" flags=[{string.Join(",", root.PrecisionFlags)}]";
        return line;
    }

    public static string Format(RootDiscoveryDiagnostic diagnostic) => $"{diagnostic.Code} {diagnostic.AffectedScope}";

    private static RootDiscoveryResult Discover(IExecutionRootProvider provider, ProviderCase testCase, string linePrefix)
    {
        var solution = testCase.Projects.Count == 0
            ? FixtureSolution.Create(Options(testCase), testCase.Sources.Select(source => (source.Path, linePrefix + source.Source)).ToArray())
            : FixtureSolution.CreateProjects(Options(testCase),
                                             testCase.Projects.Select(source => (source.Project, source.Path, linePrefix + source.Source)).ToArray());
        var compilations = solution.Projects
                                   .Select(project => project.GetCompilationAsync().GetAwaiter().GetResult()
                                                      ?? throw new InvalidOperationException($"Project {project.Name} has no compilation."))
                                   .ToArray();
        var diIndex = DiIndexBuilder.Build(SCOPE_ID, compilations, ROOT_DIRECTORY, CancellationToken.None);
        return provider.Discover(new RootDiscoveryContext(SCOPE_ID, compilations, ROOT_DIRECTORY, diIndex, CancellationToken.None));
    }

    private static FixtureOptions Options(ProviderCase testCase)
    {
        var match = TargetFramework().Match(testCase.TargetFramework);
        if (!match.Success)
            throw new ArgumentException($"Target framework '{testCase.TargetFramework}' is not net<major>.0.", nameof(testCase));

        var major = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var versions = StubAssemblies.Names.ToDictionary(name => name, name => StubAssemblies.IsFramework(name) ? major : StubAssemblies.DefaultVersion(name),
                                                         StringComparer.Ordinal);
        foreach (var (name, version) in testCase.AssemblyVersions)
            versions[name] = version;
        return new FixtureOptions
        {
            StubVersions = versions,
            ExtraAssemblyNames = testCase.AssemblyVersions.Keys.Except(StubAssemblies.Names, StringComparer.Ordinal).ToArray(),
            OmittedStubs = testCase.OmittedAssemblies,
            ProjectStubVersions = testCase.ProjectAssemblyVersions,
            ProjectOmittedStubs = testCase.ProjectOmittedAssemblies
        };
    }

    private static string[] Sorted(IEnumerable<string> lines) => lines.Order(StringComparer.Ordinal).ToArray();

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*_[A-Za-z0-9]+_[A-Za-z0-9]+$")]
    private static partial Regex CaseName();

    [GeneratedRegex(@"^net(\d+)\.0$")]
    private static partial Regex TargetFramework();
}
