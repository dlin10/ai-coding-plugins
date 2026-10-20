using System.Text;
using System.Text.Json.Nodes;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

/// <summary>What a generated file accepts (R2), over the header, the kind of entry, the identity of its member, its content and how
/// often its member is named. Validity is <see cref="GeneratedModelFiles.Valid"/>'s, applicability <see cref="GeneratedModelFiles.Applies"/>'s;
/// the expected answer of a cell is written here from the requirement, not from the code.</summary>
public sealed class GeneratedEntryAcceptanceMatrixTests
{
    private const string RUN = "M:Lib.Api.Run(System.Action{System.Int32},System.Object,Lib.Box)";
    private const string OTHER = "M:Lib.Api.Other(System.Object)";
    private const string MISSING = "M:Lib.Api.Missing(System.Object)";
    private const string BYSTANDER = "M:Lib.Api.Third(System.Object)";
    private const string SOURCE = """
        [assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
        namespace Lib
        {
            // Nothing derives from a Box, and it has no CompareTo: its objects can run an Equals and no CompareTo.
            public sealed class Box { }

            public static class Api
            {
                public static void Run(System.Action<int> callback, object other, Box box) { }
                public static void Other(object value) { }
                public static void Third(object value) { }
            }
        }
        """;

    private static readonly Guid MVID = Guid.Parse("5c8d0b6e-7a4f-4b1e-9f2a-3d6c1e8b0a47");
    private static readonly GeneratedFileKey PackageKey = GeneratedFileKey.ForPackage(GenerationRuns.ASSEMBLY, new Version(1, 0, 0, 0), "Fixture.Package", "2.1.0", MVID);
    private static readonly GeneratedFileKey FrameworkKey = GeneratedFileKey.ForFramework(GenerationRuns.ASSEMBLY, new Version(1, 0, 0, 0), "Microsoft.NETCore.App", 8);

    private static readonly Lazy<(Compilation Compilation, IMethodSymbol Run, IMethodSymbol Third)> Library = new(() =>
    {
        var compilation = GenerationRuns.Compile(SOURCE).Compilation ?? throw new InvalidOperationException("The fixture library does not compile.");
        var type = compilation.GetTypeByMetadataName("Lib.Api")!;
        var run = type.GetMembers("Run").OfType<IMethodSymbol>().Single();
        Assert.Equal(RUN, run.GetDocumentationCommentId());
        return (compilation, run, type.GetMembers("Third").OfType<IMethodSymbol>().Single());
    });

    private static readonly HeaderVariant[] Headers = Enum.GetValues<HeaderVariant>();
    private static readonly EntryContent[] ModelContents =
    [
        EntryContent.Sound, EntryContent.OutsideVocabulary, EntryContent.MarkedOpaque, EntryContent.UnknownMember, EntryContent.OtherAssembly,
        EntryContent.VersionOutsideRange, EntryContent.UnknownParameter, EntryContent.ModelledCallOnMissingParameter, EntryContent.ModelledCallFault,
        EntryContent.ModelledCallOfPresentMember, EntryContent.ModelledCallOnAnyObject
    ];

    private static readonly EntryContent[] RefusalContents = [EntryContent.KeptWord, EntryContent.UnkeptKnownWord, EntryContent.UnknownWord];

    private static readonly IReadOnlyList<Cell> Cells = Headers
        .SelectMany(header => Enum.GetValues<Multiplicity>().SelectMany(multiplicity =>
            ModelContents.SelectMany(content => Enum.GetValues<Identity>().Select(identity => new Cell(header, EntryKind.Model, identity, content, multiplicity)))
                         .Concat(RefusalContents.Select(content => new Cell(header, EntryKind.Refusal, Identity.OuterEqualsInner, content, multiplicity)))))
        .ToArray();

    private enum HeaderVariant
    {
        PackageMatching,
        PackageAssemblyName,
        PackageAssemblyVersion,
        PackageId,
        PackageVersion,
        PackageMvid,
        FrameworkMatching,
        FrameworkAssemblyName,
        FrameworkAssemblyVersion,
        FrameworkName,
        FrameworkMajor
    }

    private enum EntryKind { Model, Refusal }

    private enum Identity { OuterEqualsInner, OuterDiffers }

    private enum EntryContent
    {
        // A model: sound, the two validity faults, the six applicability faults, then two modelled calls that are no fault.
        Sound,
        OutsideVocabulary,
        MarkedOpaque,
        UnknownMember,
        OtherAssembly,
        VersionOutsideRange,
        UnknownParameter,
        ModelledCallOnMissingParameter,

        // A modelled call to a member that does not exist: a CompareTo on an object of a sealed type that has none.
        ModelledCallFault,

        // A modelled call whose member the receiver's type has (an Equals on a Box), and one on an object that stands for any object, where
        // a subtype may have it (a CompareTo on an object): both fit.
        ModelledCallOfPresentMember,
        ModelledCallOnAnyObject,

        // A refusal: a word a file keeps, a known word it does not keep, a word nobody knows.
        KeptWord,
        UnkeptKnownWord,
        UnknownWord
    }

    private enum Multiplicity { Once, Twice }

    private enum Answer { Unreadable, Invalid, NotApplicable, Applies }

    private sealed record Cell(HeaderVariant Header, EntryKind Kind, Identity Identity, EntryContent Content, Multiplicity Multiplicity)
    {
        public override string ToString() => $"{Header} / {Kind} / {Identity} / {Content} / {Multiplicity}";
    }

    [Fact]
    public void Every_cell_gets_its_expected_answer()
    {
        var wrong = new List<string>();
        foreach (var cell in Cells)
        {
            var (answer, file) = Actual(cell);
            if (answer != Expected(cell))
            {
                var why = file.Entries.TryGetValue(OuterMember(cell), out var entry) &&
                          !GeneratedModelFiles.Applies(entry, Library.Value.Run, Library.Value.Compilation, out var diagnostic) ? diagnostic : string.Join(" | ", file.Diagnostics);
                wrong.Add($"{cell}: expected {Expected(cell)}, got {answer}: {why}");
            }
            if (file.IsUnreadable)
                continue;
            // Whatever the entry holds, the entries beside it stand.
            if (!file.Entries.TryGetValue(BYSTANDER, out var bystander) || !GeneratedModelFiles.Applies(bystander, Library.Value.Third, Library.Value.Compilation, out _))
                wrong.Add($"{cell}: the entry beside it was lost");
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} of {Cells.Count} cells:\n" + string.Join('\n', wrong.Take(20)));
    }

    [Fact]
    public void No_cell_accepts_what_R2_rejects()
    {
        var accepted = new List<string>();
        foreach (var cell in Cells)
        {
            var (answer, file) = Actual(cell);
            var describes = file.Entries.ContainsKey(OuterMember(cell));
            switch (Expected(cell))
            {
                case Answer.Unreadable:
                    // The whole file counts as empty, whatever its entries, and says why once: the part of the header that differs.
                    if (describes || file.Entries.Count != 0 || file.UnreadableReason is null || file.Diagnostics.Count != 1 ||
                        !file.UnreadableReason.Contains(DifferingPart(cell.Header), StringComparison.Ordinal))
                        accepted.Add($"{cell}: an unreadable file holds entries or is not diagnosed ({file.UnreadableReason})");
                    break;
                case Answer.Invalid:
                    // The entry is absent and diagnosed by name and for its reason; a member named twice is diagnosed for each of its entries.
                    var invalid = file.Diagnostics.Where(diagnostic => diagnostic.Contains(OuterMember(cell), StringComparison.Ordinal)).ToArray();
                    if (describes || invalid.Length != (cell.Multiplicity == Multiplicity.Twice ? 2 : 1) ||
                        invalid.Any(diagnostic => !diagnostic.Contains(InvalidBecause(cell), StringComparison.Ordinal)))
                        accepted.Add($"{cell}: an invalid entry is accepted or not diagnosed ({string.Join(" | ", file.Diagnostics)})");
                    break;
                case Answer.NotApplicable:
                    // Valid and kept, and no call of the compilation is described by it: the diagnostic names the compilation and the fault.
                    string? why = null;
                    if (!describes || answer != Answer.NotApplicable ||
                        GeneratedModelFiles.Applies(file.Entries[OuterMember(cell)], Library.Value.Run, Library.Value.Compilation, out why) ||
                        why is null || !why.Contains(GenerationRuns.ASSEMBLY, StringComparison.Ordinal) ||
                        !why.Contains(InapplicableBecause(cell.Content), StringComparison.Ordinal))
                        accepted.Add($"{cell}: an entry that does not apply is not told so ({why})");
                    break;
            }
        }

        Assert.True(accepted.Count == 0, $"{accepted.Count} cells:\n" + string.Join('\n', accepted.Take(20)));
    }

    [Fact]
    public void Every_axis_value_occurs()
    {
        Assert.Equal(Headers.Length * 2 * (ModelContents.Length * 2 + RefusalContents.Length), Cells.Count);
        Assert.Equal(Cells.Count, Cells.Distinct().Count());
        Assert.Equal(Headers.Order(), Cells.Select(cell => cell.Header).Distinct().Order());
        Assert.Equal(Enum.GetValues<EntryKind>().Order(), Cells.Select(cell => cell.Kind).Distinct().Order());
        Assert.Equal(Enum.GetValues<Identity>().Order(), Cells.Select(cell => cell.Identity).Distinct().Order());
        Assert.Equal(Enum.GetValues<EntryContent>().Order(), Cells.Select(cell => cell.Content).Distinct().Order());
        Assert.Equal(Enum.GetValues<Multiplicity>().Order(), Cells.Select(cell => cell.Multiplicity).Distinct().Order());
        Assert.Equal(Enum.GetValues<Answer>().Order(), Cells.Select(Expected).Distinct().Order());

        // Each content is met with both identities (a model) and both multiplicities, and each header with every answer it can give.
        Assert.All(ModelContents, content => Assert.Equal(Enum.GetValues<Identity>().Order(), Cells.Where(cell => cell.Content == content)
                                                                                                  .Select(cell => cell.Identity).Distinct().Order()));
        Assert.All(Cells.Select(cell => cell.Content).Distinct(), content =>
            Assert.Equal(Enum.GetValues<Multiplicity>().Order(), Cells.Where(cell => cell.Content == content).Select(cell => cell.Multiplicity).Distinct().Order()));
        Assert.All(Headers.Where(header => !Matches(header)), header =>
            Assert.Equal([Answer.Unreadable], Cells.Where(cell => cell.Header == header).Select(Expected).Distinct()));
        Assert.All(Headers.Where(Matches), header =>
            Assert.Equal(Enum.GetValues<Answer>().Where(answer => answer != Answer.Unreadable).Order(),
                         Cells.Where(cell => cell.Header == header).Select(Expected).Distinct().Order()));
    }

    /// <summary>R2, from the requirement: a header that differs in any part makes the whole file unreadable; with a matching header a
    /// member named twice has both entries invalid; otherwise a model whose member differs from its entry's, or with a validity fault, is
    /// invalid and so is a refusal of a word no file keeps; a valid model with an applicability fault — among them a modelled call to a
    /// member its receiver's type does not have — is kept and applies to no call of the compilation; a sound model, one whose modelled
    /// calls fit and a refusal of a kept word apply.</summary>
    /// <param name="cell">The cell.</param>
    private static Answer Expected(Cell cell) =>
        !Matches(cell.Header) ? Answer.Unreadable :
        cell.Multiplicity == Multiplicity.Twice ? Answer.Invalid :
        cell.Kind == EntryKind.Refusal ? cell.Content == EntryContent.KeptWord ? Answer.Applies : Answer.Invalid :
        cell.Identity == Identity.OuterDiffers ? Answer.Invalid :
        cell.Content switch
        {
            EntryContent.Sound or EntryContent.ModelledCallOfPresentMember or EntryContent.ModelledCallOnAnyObject => Answer.Applies,
            EntryContent.OutsideVocabulary or EntryContent.MarkedOpaque => Answer.Invalid,
            _ => Answer.NotApplicable
        };

    private static bool Matches(HeaderVariant header) => header is HeaderVariant.PackageMatching or HeaderVariant.FrameworkMatching;

    /// <summary>The words of the part of a header that differs from the file's key.</summary>
    /// <param name="header">The header variant, one that differs.</param>
    private static string DifferingPart(HeaderVariant header) => header switch
    {
        HeaderVariant.PackageAssemblyName or HeaderVariant.FrameworkAssemblyName => "assembly name",
        HeaderVariant.PackageAssemblyVersion or HeaderVariant.FrameworkAssemblyVersion => "assembly version",
        HeaderVariant.PackageId => "package id",
        HeaderVariant.PackageVersion => "package version",
        HeaderVariant.PackageMvid => "mvid",
        HeaderVariant.FrameworkName => "framework name",
        HeaderVariant.FrameworkMajor => "framework major",
        _ => throw new ArgumentOutOfRangeException(nameof(header), header, "A matching header has no differing part.")
    };

    /// <summary>The words that say why an entry of an invalid cell is invalid: the first fault the reading meets.</summary>
    /// <param name="cell">The cell, one whose answer is invalid.</param>
    private static string InvalidBecause(Cell cell) =>
        cell.Multiplicity == Multiplicity.Twice ? "more than one entry" :
        cell.Kind == EntryKind.Refusal ? "does not keep the reason" :
        cell.Content switch
        {
            EntryContent.OutsideVocabulary => "does not parse",
            EntryContent.MarkedOpaque => "opaque",
            _ => "its model describes"
        };

    /// <summary>The words that say why a valid entry applies to no call of the compilation.</summary>
    /// <param name="content">The content, one with an applicability fault.</param>
    private static string InapplicableBecause(EntryContent content) => content switch
    {
        EntryContent.UnknownMember => "another member",
        EntryContent.OtherAssembly => "declaring assembly",
        EntryContent.VersionOutsideRange => "outside the entry's versions",
        EntryContent.UnknownParameter => "missing parameter",
        EntryContent.ModelledCallOnMissingParameter => "names no parameter",
        EntryContent.ModelledCallFault => "names a member that Lib.Box does not have",
        _ => throw new ArgumentOutOfRangeException(nameof(content), content, "This content has no applicability fault.")
    };

    private static (Answer Answer, GeneratedModelFile File) Actual(Cell cell)
    {
        var key = cell.Header <= HeaderVariant.PackageMvid ? PackageKey : FrameworkKey;
        var file = GeneratedModelFiles.Valid(key, "gen/cell.json", Encoding.UTF8.GetBytes(Document(cell)));
        if (file.IsUnreadable)
            return (Answer.Unreadable, file);
        if (!file.Entries.TryGetValue(OuterMember(cell), out var entry))
            return (Answer.Invalid, file);
        return (GeneratedModelFiles.Applies(entry, Library.Value.Run, Library.Value.Compilation, out _) ? Answer.Applies : Answer.NotApplicable, file);
    }

    /// <summary>The member the entry of the cell is named by in the file: the one the model describes, or the one it is made to differ
    /// from.</summary>
    /// <param name="cell">The cell.</param>
    private static string OuterMember(Cell cell) =>
        cell.Kind == EntryKind.Model && cell.Identity == Identity.OuterEqualsInner && cell.Content == EntryContent.UnknownMember ? MISSING : RUN;

    private static string Document(Cell cell)
    {
        var outer = OuterMember(cell);
        var entries = new JsonArray { Entry(cell, outer) };
        if (cell.Multiplicity == Multiplicity.Twice)
            entries.Add(new JsonObject { ["member"] = outer, ["model"] = Sound(outer, "other") });
        entries.Add(new JsonObject { ["member"] = BYSTANDER, ["model"] = Sound(BYSTANDER, "value") });
        return new JsonObject { ["schemaVersion"] = 2, ["generator"] = GeneratorVersion.Fingerprint, ["assembly"] = Header(cell.Header), ["members"] = entries }.ToJsonString();
    }

    private static JsonObject Entry(Cell cell, string outer)
    {
        if (cell.Kind == EntryKind.Refusal)
        {
            var reason = cell.Content switch
            {
                EntryContent.KeptWord => ModelReasons.UNKNOWN_TOUCH,
                EntryContent.UnkeptKnownWord => GenerationReasons.NO_IMPLEMENTATION,
                _ => "mystery"
            };
            return new JsonObject
            {
                ["member"] = outer,
                ["refusal"] = new JsonObject { ["reason"] = reason, ["detail"] = "in words", ["causes"] = new JsonArray("probe-handed-to-unseen") }
            };
        }

        var model = Sound(RUN, "other");
        switch (cell.Content)
        {
            case EntryContent.OutsideVocabulary:
                model["result"] = "mystery(arg:other)";
                break;
            case EntryContent.MarkedOpaque:
                model.Remove("effects");
                model.Remove("fates");
                model["opaque"] = true;
                break;
            case EntryContent.OtherAssembly:
                model["assemblies"] = new JsonArray("Other.Library");
                break;
            case EntryContent.VersionOutsideRange:
                model["versions"] = new JsonObject { ["minimum"] = "2.0.0.0", ["maximumExclusive"] = "3.0.0.0" };
                break;
            case EntryContent.UnknownParameter:
                model["effects"] = new JsonObject { ["nope"] = new JsonArray("reads-deep") };
                break;
            case EntryContent.ModelledCallOnMissingParameter:
                model["calls"] = new JsonArray(new JsonObject { ["member"] = "GetHashCode", ["on"] = "arg:missing" });
                break;
            case EntryContent.ModelledCallFault:
                model["calls"] = new JsonArray(Call("CompareTo", "arg:box"));
                break;
            case EntryContent.ModelledCallOfPresentMember:
                model["calls"] = new JsonArray(Call("Equals", "arg:box"));
                break;
            case EntryContent.ModelledCallOnAnyObject:
                model["calls"] = new JsonArray(Call("CompareTo", "arg:other"));
                break;
        }

        // The model describes the member the entry is named by, or another one; an unknown member is one the compilation does not have.
        model["member"] = cell.Content == EntryContent.UnknownMember ? MISSING : cell.Identity == Identity.OuterEqualsInner ? RUN : OTHER;
        return new JsonObject { ["member"] = outer, ["model"] = model };
    }

    /// <summary>A modelled call of a member on the objects a value names, with the value as its one input.</summary>
    /// <param name="member">The member's name, one that takes one parameter.</param>
    /// <param name="value">The value the member runs on and is handed.</param>
    private static JsonObject Call(string member, string value) =>
        new() { ["member"] = member, ["on"] = value, ["inputs"] = new JsonArray(new JsonArray(value)) };

    /// <summary>A model that is sound for <c>Run</c> when the parameter it reads is <c>other</c>, and for the members beside it when it is
    /// <c>value</c>; <c>Run</c>'s delegate parameter has the fate the vocabulary asks of it.</summary>
    /// <param name="member">The member the model describes.</param>
    /// <param name="parameter">The parameter the model reads deeply.</param>
    private static JsonObject Sound(string member, string parameter) =>
        ModelEntryWriter.Write(new LibraryModel(member, [new SupportedAssemblyVersion(GenerationRuns.ASSEMBLY, new Version(1, 0, 0, 0), new Version(2, 0, 0, 0))],
                                                [LibraryEffect.DeepReadOf(parameter)])
        {
            Fates = parameter == "other" ? [new LibraryFate("callback", LibraryFateKind.InvokeNow, null, null)] : []
        });

    /// <summary>The header of the file the cell's key names, with at most one part changed.</summary>
    /// <param name="variant">The header variant.</param>
    private static JsonObject Header(HeaderVariant variant)
    {
        var key = variant <= HeaderVariant.PackageMvid ? PackageKey : FrameworkKey;
        var header = new JsonObject { ["name"] = key.AssemblyName, ["version"] = key.AssemblyVersion.ToString(4) };
        if (key.IsPackage)
        {
            header["mvid"] = key.Mvid!.Value.ToString("D");
            header["package"] = key.Package;
            header["packageVersion"] = key.PackageVersion;
        }
        else
        {
            header["mvid"] = null;
            header["framework"] = key.Framework;
            header["major"] = key.Major;
        }

        switch (variant)
        {
            case HeaderVariant.PackageAssemblyName or HeaderVariant.FrameworkAssemblyName:
                header["name"] = "Other.Library";
                break;
            case HeaderVariant.PackageAssemblyVersion or HeaderVariant.FrameworkAssemblyVersion:
                header["version"] = "1.0.0.1";
                break;
            case HeaderVariant.PackageId:
                header["package"] = "Other.Package";
                break;
            case HeaderVariant.PackageVersion:
                header["packageVersion"] = "2.1.1";
                break;
            case HeaderVariant.PackageMvid:
                header["mvid"] = "0f0e0d0c-0b0a-4908-8706-050403020100";
                break;
            case HeaderVariant.FrameworkName:
                header["framework"] = "Microsoft.AspNetCore.App";
                break;
            case HeaderVariant.FrameworkMajor:
                header["major"] = 9;
                break;
        }

        return header;
    }
}
