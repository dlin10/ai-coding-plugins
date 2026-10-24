using System.Collections.Concurrent;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>Every place where the driver cancels work it began — a trigger variant, a seed of a field, an event or an element, a
/// witness's property, body or value, a trigger whose call does not compile — leaves the driver it would build without the work.</summary>
public sealed class DriverRollbackMatrixTests
{
    private enum Site { TriggerVariant, SeedField, SeedEvent, SeedElement, PropertyWitness, WitnessBody, WitnessValue, UncompilableTrigger }

    private static readonly Site[] Cells = Enum.GetValues<Site>();

    /// <summary>The types every cell shares: <c>V</c>, a holding value no source builds; <c>NeedsV</c>, a class a seed derives from
    /// whose constructor needs one; <c>Pointed</c>, a delegate whose witness names a type the driver cannot; <c>NeedsUser</c>, a value
    /// that builds a user's object before it needs a <c>V</c>.</summary>
    private const string PRELUDE = """
        using System;
        using System.Collections.Generic;

        namespace Lib
        {
            public sealed class V { public Action Slot; private V() { } }
            public abstract class NeedsV { protected NeedsV(V v) { } }
            public unsafe delegate void Pointed(delegate*<void> f);
            public interface IUser { void Use(); }
            public sealed class NeedsUser { public NeedsUser(IUser user, V v) { } }
            [Obsolete("gone", true)] public interface IOld { void Try(); }

        """;

    [Fact]
    public void Every_site_leaves_the_driver_it_would_build_without_the_work()
    {
        var wrong = new ConcurrentQueue<string>();
        Parallel.ForEach(Cells, CellParallelism, site =>
        {
            try { Check(site); }
            catch (Exception error) { wrong.Enqueue($"{site}: {error.Message}"); }
        });
        Assert.True(wrong.IsEmpty, $"{wrong.Count} of {Cells.Length} cells:\n" + string.Join('\n', wrong));
    }

    [Fact]
    public void Every_site_occurs_in_a_cell()
    {
        Assert.Equal(Enum.GetValues<Site>(), Cells.Distinct().Order());
    }

    [Fact]
    public void A_failed_array_seed_leaves_no_assignment_in_the_source()
    {
        // A V[] gets an array whose one cell only a default could fill: the seed is cancelled, in setup or inside an element loop.
        (string Types, string Path)[] cases =
        [
            ("public sealed class Holder { public V[] Values; public Holder() { } }", "arg:holder/F:Lib.Holder.Values"),
            ("public sealed class Inner { public V[] Values; public Inner() { } } public sealed class Holder { public Inner[] Inners; public Holder() { } }",
             "arg:holder/F:Lib.Holder.Inners/F:Lib.Inner.Values")
        ];
        foreach (var (types, path) in cases)
        {
            var driver = Synthesize(types + " public static class Api { public static void Run(Holder holder) { } }", "M:Lib.Api.Run(Lib.Holder)");

            Assert.Contains(driver.Unseeded, unseeded => unseeded.StartsWith(path, StringComparison.Ordinal));
            Assert.DoesNotContain(".Values = ", driver.Source);
            Assert.DoesNotContain("{ default }", driver.Source);
            Assert.DoesNotContain(driver.SeedStatements, seed => seed.Path == path);
        }
    }

    [Fact]
    public void A_rejected_candidate_leaves_the_unseeded_paths_and_the_names_as_they_were()
    {
        // The rejected constructor or factory declares a probe class for its IUser, then only a default could fill its V; the accepted
        // one declares that class again, under the name the rejected one took.
        (string Rejected, string Accepted)[] cases =
        [
            ("public Built(IUser user, V v) { }", "public Built(IUser user, string s, int n) { }"),
            ("public static Built Make(IUser user, V v) => null;", "private Built() { } public static Built Make(IUser user, string s, int n) => null;")
        ];
        static string Library(string candidates) =>
            $"public sealed class Built {{ public NeedsV Field; {candidates} }} public static class Api {{ public static void Run(Built built) {{ }} }}";
        // bounded: the two cases above.
        foreach (var (rejected, accepted) in cases)
        {
            var cancelled = Synthesize(Library(rejected + " " + accepted), "M:Lib.Api.Run(Lib.Built)");
            var absent = Synthesize(Library(accepted), "M:Lib.Api.Run(Lib.Built)");

            Assert.Equal(absent.Source, cancelled.Source);
            Assert.Contains("Probe_built", DeclaredClasses(cancelled));
            Assert.Equal(absent.Unseeded, cancelled.Unseeded);
            Assert.Contains("arg:built/F:Lib.Built.Field", cancelled.Unseeded);
        }
    }

    [Fact]
    public void Seed_operations_name_only_statements_in_the_source()
    {
        var driver = Synthesize("""
            public sealed class Inner { public V[] Values; public Action B; public Inner() { } }
            public sealed class Holder
            {
                public Action A; public NeedsV Field; public Inner[] Inners; public event Pointed Changed; public event Action Done;
                public Holder() { }
            }
            public static class Api { public static void Run(Holder holder) { } }
            """, "M:Lib.Api.Run(Lib.Holder)");

        var tree = driver.Compilation.SyntaxTrees.Single();
        var setup = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.Text == DriverSynthesizer.SETUP);
        var library = driver.Member.ContainingAssembly;
        var seedAssignments = driver.Compilation.GetSemanticModel(tree).GetOperation(setup)!.Descendants()
                                    .Count(operation => operation switch
                                    {
                                        ISimpleAssignmentOperation { Target: IMemberReferenceOperation target } =>
                                            SymbolEqualityComparer.Default.Equals(target.Member.ContainingAssembly, library),
                                        IEventAssignmentOperation { EventReference: IEventReferenceOperation reference } =>
                                            SymbolEqualityComparer.Default.Equals(reference.Event.ContainingAssembly, library),
                                        _ => false
                                    });

        Assert.NotEmpty(driver.SeedStatements);
        Assert.Equal(seedAssignments, driver.SeedStatements.Count);
        Assert.All(driver.SeedStatements, seed =>
        {
            Assert.Same(tree, seed.Operation.Syntax.SyntaxTree);
            Assert.Contains(seed.Operation.Syntax.ToString(), driver.Source);
            Assert.DoesNotContain(seed.Path, driver.Unseeded);
        });
        Assert.Contains("arg:holder/F:Lib.Holder.Field", driver.Unseeded);
        Assert.Contains("arg:holder/E:Lib.Holder.Changed", driver.Unseeded);
        Assert.Contains(driver.Unseeded, path => path.StartsWith("arg:holder/F:Lib.Holder.Inners/F:Lib.Inner.Values", StringComparison.Ordinal));
        Assert.DoesNotContain(".Values = ", driver.Source);
    }

    [Fact]
    public void A_rebuild_keeps_the_members_past_the_trigger_cap_uncovered()
    {
        // 25 members of its own, none of object's (the holder is sealed and overrides none): IOld.Try, then IMany's 24, each
        // implemented explicitly so that Try comes first, the 25th past the 24 triggers of the first build. The rebuild without Try
        // keeps IMany.M23 past them, though the exclusion freed a number.
        var declared = string.Concat(Enumerable.Range(0, 24).Select(index => $"void M{index:00}(); "));
        var members = string.Concat(Enumerable.Range(0, 24).Select(index => $"void IMany.M{index:00}() {{ }} "));
        var driver = Synthesize($$"""
            public interface IMany { {{declared}} }
            [Obsolete("old")] public sealed class Holder : IOld, IMany { public Holder() { } void IOld.Try() { } {{members}} }
            [Obsolete("old")] public static class Api { public static Holder Make(Action a) => new Holder(); }
            """, "M:Lib.Api.Make(System.Action)");

        Assert.Equal(23, driver.Triggers.Count);
        Assert.DoesNotContain(driver.Triggers, trigger => trigger.Member == "M:Lib.IOld.Try");
        Assert.DoesNotContain(driver.Triggers, trigger => trigger.Member.StartsWith("M:System.Object.", StringComparison.Ordinal));
        var uncovered = driver.Uncovered[FateClassifier.RESULT];
        Assert.Contains(uncovered, why => why.StartsWith("M:Lib.IOld.Try: the driver's call does not compile: CS0619", StringComparison.Ordinal));
        Assert.Contains("M:Lib.IMany.M23: past the driver's 24 triggers", uncovered);
        Assert.Equal(2, uncovered.Count);
    }

    [Fact]
    public void An_uncompilable_member_of_one_holder_stays_a_trigger_of_the_other()
    {
        // Put takes the holder's T: a probe on the receiver Box<T>, a Wrap on the result Box<Wrap>, whose only constructor is
        // obsolete as an error, so only the result's call does not compile.
        var driver = Synthesize("""
            public sealed class Wrap { public object Held; [Obsolete("gone", true)] public Wrap() { } }
            public sealed class Box<T> { public Box() { } public Box<Wrap> Convert(Action a) => null; public void Put(T value) { } }
            """, "M:Lib.Box`1.Convert(System.Action)");

        Assert.Contains(driver.Uncovered[FateClassifier.RESULT],
                        why => why.StartsWith("M:Lib.Box`1.Put(`0): the driver's call does not compile: CS0619", StringComparison.Ordinal));
        Assert.DoesNotContain(driver.Triggers, trigger => trigger.Holder == FateClassifier.RESULT && trigger.Member == "M:Lib.Box`1.Put(`0)");
        Assert.Contains(driver.Triggers, trigger => trigger.Holder == FateClassifier.THIS && trigger.Member == "M:Lib.Box`1.Put(`0)");
        Assert.DoesNotContain(driver.Uncovered[FateClassifier.THIS], why => why.StartsWith("M:Lib.Box`1.Put(`0)", StringComparison.Ordinal));
    }

    private static void Check(Site site)
    {
        var (types, memberId) = Fixture(site, present: true);
        var cancelled = Synthesize(types, memberId);
        var (absentTypes, absentMemberId) = Fixture(site, present: false);
        var absent = Synthesize(absentTypes, absentMemberId);

        Assert.Equal(absent.Source, WithoutCancelledDeclaration(site, cancelled.Source));
        Assert.Equal(absent.SeedStatements.Select(seed => (seed.Path, seed.Operation.Syntax.ToString())),
                     cancelled.SeedStatements.Select(seed => (seed.Path, seed.Operation.Syntax.ToString())));
        Assert.Equal(DeclaredClasses(absent), DeclaredClasses(cancelled));
        Assert.Equal(absent.Triggers, cancelled.Triggers);

        var (cancelledOnly, absentOnly) = UnseededPaths(site);
        Assert.Equal(cancelledOnly, cancelled.Unseeded.Except(absent.Unseeded).Order(StringComparer.Ordinal));
        Assert.Equal(absentOnly, absent.Unseeded.Except(cancelled.Unseeded).Order(StringComparer.Ordinal));
        var uncovered = cancelled.Uncovered.SelectMany(holder => holder.Value.Except(absent.Uncovered.GetValueOrDefault(holder.Key) ?? [])).ToArray();
        Assert.Empty(absent.Uncovered.SelectMany(holder => holder.Value.Except(cancelled.Uncovered.GetValueOrDefault(holder.Key) ?? [])));
        if (UncoveredMember(site) is { } member)
            Assert.StartsWith(member + ": ", Assert.Single(uncovered));
        else
            Assert.Empty(uncovered);
    }

    /// <summary>The library of a cell: with the work the driver cancels at the site, or without it.</summary>
    /// <param name="site">The site.</param>
    /// <param name="present">Whether the library has the member, field, event or element whose work is cancelled.</param>
    private static (string Types, string MemberId) Fixture(Site site, bool present)
    {
        string When(string text) => present ? text : "";
        const string RUN_HOLDER = "public static class Api { public static void Run(Holder holder) { } }";
        const string RUN_USER = "public static class Api { public static void Run(IWitnessed user) { } }";
        return site switch
        {
            // The trigger builds a user's object for its first argument, then only a default could fill its V.
            Site.TriggerVariant => ($$"""
                public sealed class Holder { public Holder() { } {{When("public void Bad(IUser user, V missing) { }")}} public void Good(IUser user) { } }
                public static class Api { public static Holder Make(Action a) => new Holder(); }
                """, "M:Lib.Api.Make(System.Action)"),
            // The seed of NeedsV declares a subclass whose base constructor only a default could fill.
            Site.SeedField => ($"public sealed class Holder {{ public Holder() {{ }} {When("public NeedsV Field;")} public Action Later; }} {RUN_HOLDER}",
                               "M:Lib.Api.Run(Lib.Holder)"),
            // The witness subscribed to the event names a function pointer, which the driver cannot.
            Site.SeedEvent => ($"public sealed class Holder {{ public Holder() {{ }} {When("public event Pointed Changed;")} public Action Later; }} {RUN_HOLDER}",
                               "M:Lib.Api.Run(Lib.Holder)"),
            // A list setup cannot replace gets a V only a default could fill; without the work, the same container is one the driver
            // adds no element to, an interface, whose elements are seeded alike.
            Site.SeedElement => ($"public sealed class Holder {{ public Holder() {{ }} public readonly {(present ? "List<V>" : "IList<V>")} Items = new List<V>(); public Action Later; }} {RUN_HOLDER}",
                                 "M:Lib.Api.Run(Lib.Holder)"),
            // The getter's value only a default could fill, so the setter's witness stores go with it, and the indexer is extern. An
            // indexer, not a property, so that no seed path runs through it.
            Site.PropertyWitness => ($"public interface IWitnessed {{ {When("V this[int index] { get; set; }")} IUser Later(); }} {RUN_USER}",
                                     "M:Lib.Api.Run(Lib.IWitnessed)"),
            // The witness stores its receiver and first argument, then only a default could fill its out V.
            Site.WitnessBody => ($"public interface IWitnessed {{ {When("void Cancelled(object first, out V result);")} IUser Later(); }} {RUN_USER}",
                                 "M:Lib.Api.Run(Lib.IWitnessed)"),
            // The witness's return builds a user's object, then only a default could fill its V.
            Site.WitnessValue => ($"public interface IWitnessed {{ {When("NeedsUser Cancelled();")} IUser Later(); }} {RUN_USER}",
                                  "M:Lib.Api.Run(Lib.IWitnessed)"),
            // The cast to an interface obsolete as an error does not compile.
            _ => ($$"""
                [Obsolete("old")] public sealed class Holder{{When(" : IOld")}} { public Holder() { } {{When("void IOld.Try() { }")}} public void Fire() { } }
                [Obsolete("old")] public static class Api { public static Holder Make(Action a) => new Holder(); }
                """, "M:Lib.Api.Make(System.Action)")
        };
    }

    /// <summary>The paths only one cell of the pair leaves unseeded: the cancelled seed's own path, and for a cancelled element the
    /// container's, which the interface container of the cell without the work records itself, as a field setup cannot write.</summary>
    /// <param name="site">The site.</param>
    private static (string[] CancelledOnly, string[] AbsentOnly) UnseededPaths(Site site) => site switch
    {
        Site.SeedField => (["arg:holder/F:Lib.Holder.Field"], []),
        Site.SeedEvent => (["arg:holder/E:Lib.Holder.Changed"], []),
        Site.SeedElement => ([], ["arg:holder/F:Lib.Holder.Items"]),
        _ => ([], [])
    };

    /// <summary>The member only the cancelled cell leaves uncovered, or <c>null</c>.</summary>
    /// <param name="site">The site.</param>
    private static string? UncoveredMember(Site site) => site switch
    {
        Site.TriggerVariant => "M:Lib.Holder.Bad(Lib.IUser,Lib.V)",
        Site.UncompilableTrigger => "M:Lib.IOld.Try",
        _ => null
    };

    /// <summary>The source without the witness member whose bodies were cancelled: it stays declared, <c>extern</c>, as an interface
    /// member the driver's class must implement.</summary>
    /// <param name="site">The site.</param>
    /// <param name="source">The cancelled cell's driver source.</param>
    private static string WithoutCancelledDeclaration(Site site, string source)
    {
        if (site is not (Site.PropertyWitness or Site.WitnessBody or Site.WitnessValue))
            return source;
        var marker = site == Site.PropertyWitness ? "global::Lib.IWitnessed.this[" : "Cancelled";
        var lines = source.Split('\n');
        Assert.Contains("extern ", Assert.Single(lines, line => line.Contains(marker, StringComparison.Ordinal)));
        return string.Join('\n', lines.Where(line => !line.Contains(marker, StringComparison.Ordinal)));
    }

    private static string[] DeclaredClasses(Driver driver) =>
        driver.Compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
              .Select(declaration => declaration.Identifier.Text).ToArray();

    private static Driver Synthesize(string types, string memberId)
    {
        var library = Compile(PRELUDE + types + "\n}\n");
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        var member = DriverSynthesizer.FindMember(library.Compilation, memberId);
        Assert.NotNull(member);
        var synthesis = DriverSynthesizer.Synthesize(library.Compilation, member, library.ExternMembers, library.OpenedFields, CancellationToken.None);
        Assert.True(synthesis.Driver is not null, $"{synthesis.Reason}: {synthesis.Detail}");
        Assert.Empty(synthesis.Driver.Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return synthesis.Driver;
    }
}
