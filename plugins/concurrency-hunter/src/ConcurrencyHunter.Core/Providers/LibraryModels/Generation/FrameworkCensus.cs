using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>What the owner's regeneration answered for one member of the frameworks' census.</summary>
/// <param name="LookupId">The member's declaration id in the form a run looks a call up by, with <c>~</c> and the return type.</param>
/// <param name="Framework">The framework the member's assembly belongs to.</param>
/// <param name="Assembly">The member's assembly.</param>
/// <param name="Outcome"><see cref="FrameworkCensus.MODEL"/>, or the refusal's reason.</param>
/// <param name="Causes">The codes of the refusal's causes, sorted; none for a model.</param>
/// <param name="Model">The model as a built-in entry under <paramref name="LookupId"/>, or <c>null</c> for a refusal.</param>
internal sealed record CensusAnswer(string LookupId, string Framework, string Assembly, string Outcome, IReadOnlyList<string> Causes, JsonObject? Model);

/// <summary>One member's line of the census record: what the regeneration answered and what ships for it.</summary>
/// <param name="LookupId">The member's declaration id in the run's form.</param>
/// <param name="Framework">The member's framework.</param>
/// <param name="Assembly">The member's assembly.</param>
/// <param name="Outcome"><see cref="FrameworkCensus.MODEL"/>, or the refusal's reason.</param>
/// <param name="Causes">The codes of the refusal's causes, sorted.</param>
/// <param name="Ships">What ships for the member: <see cref="FrameworkCensus.SHIPS_MODEL"/>, <see cref="FrameworkCensus.SHIPS_KEPT"/> or
/// <see cref="FrameworkCensus.SHIPS_FAILED"/>.</param>
/// <param name="ModelHash">The first twelve hex digits of the SHA-256 of the shipped model's JSON, or empty for a failed member.</param>
internal sealed record CensusRow(string LookupId, string Framework, string Assembly, string Outcome, IReadOnlyList<string> Causes, string Ships,
                                 string ModelHash)
{
    /// <summary>The row as one tab-separated line.</summary>
    internal string Line => string.Join('\t', LookupId, Framework, Assembly, Outcome, string.Join(',', Causes), Ships, ModelHash);

    /// <summary>Reads a row from its line.</summary>
    /// <param name="line">The line.</param>
    /// <exception cref="FormatException">The line does not have seven tab-separated parts.</exception>
    internal static CensusRow Parse(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length != 7)
            throw new FormatException($"A census line has seven tab-separated parts: {line}");
        return new CensusRow(parts[0], parts[1], parts[2], parts[3], parts[4].Length == 0 ? [] : parts[4].Split(','), parts[5], parts[6]);
    }
}

/// <summary>The record of a census, kept beside the CoreLib generation snapshot: the commit and the engine fingerprint it was generated
/// with, the framework patches it read, and one row per member. The release gate reads its engine fingerprint (ADR 0022).</summary>
/// <param name="Commit">The commit of the repository the generator was built from.</param>
/// <param name="Engine">The engine fingerprint: the generator version without the built-in models.</param>
/// <param name="Patches">The patch of each framework the regeneration read.</param>
/// <param name="Rows">The rows, sorted by member.</param>
internal sealed record CensusRecord(string Commit, string Engine, IReadOnlyDictionary<string, string> Patches, IReadOnlyList<CensusRow> Rows)
{
    private const string TITLE = "# framework census";
    private const string COMMIT = "# commit ";
    private const string ENGINE = "# engine ";
    private const string HEADER = "# ";

    /// <summary>The record's lines: the title, the commit, the engine, one line per framework, then the rows sorted by member.</summary>
    internal IEnumerable<string> Lines =>
        new[] { TITLE, COMMIT + Commit, ENGINE + Engine }
            .Concat(FrameworkAssemblies.FRAMEWORKS.Where(Patches.ContainsKey).Select(framework => $"{HEADER}{framework} {Patches[framework]}"))
            .Concat(Rows.OrderBy(row => row.LookupId, StringComparer.Ordinal).Select(row => row.Line));

    /// <summary>Reads a record from its lines.</summary>
    /// <param name="lines">The lines.</param>
    /// <exception cref="FormatException">A row is malformed, a member stands twice, or the record names no engine fingerprint.</exception>
    internal static CensusRecord Parse(IEnumerable<string> lines)
    {
        WorkCancellation.Check();
        var (commit, engine) = (string.Empty, string.Empty);
        var patches = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = new List<CensusRow>();
        foreach (var line in lines.Where(line => line.Length != 0))
        {
            WorkCancellation.Check();
            if (line.StartsWith(COMMIT, StringComparison.Ordinal))
                commit = line[COMMIT.Length..];
            else if (line.StartsWith(ENGINE, StringComparison.Ordinal))
                engine = line[ENGINE.Length..];
            else if (line.StartsWith(HEADER, StringComparison.Ordinal) && line[HEADER.Length..].Split(' ') is [var framework, var patch] &&
                     FrameworkAssemblies.FRAMEWORKS.Contains(framework))
                patches[framework] = patch;
            else if (!line.StartsWith(HEADER, StringComparison.Ordinal))
                rows.Add(CensusRow.Parse(line));
        }
        if (engine.Length == 0)
            throw new FormatException("The census record names no engine fingerprint.");
        if (rows.GroupBy(row => row.LookupId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice)
            throw new FormatException($"{twice.Key} stands twice in the census record.");
        return new CensusRecord(commit, engine, patches, rows.OrderBy(row => row.LookupId, StringComparer.Ordinal).ToArray());
    }
}

/// <summary>What the census tool read from the census runs: the answers by member and the problems it met.</summary>
/// <param name="Answers">The answers, one per member, sorted by member.</param>
/// <param name="Problems">What the tool could not take, in words: an entry no member of the frameworks answers to, a model that does not
/// apply to its member, two runs that answered a member differently.</param>
internal sealed record CensusHarvest(IReadOnlyList<CensusAnswer> Answers, IReadOnlyList<string> Problems);

/// <summary>One change a census makes to a built-in file. Core plans the changes and the census tool makes them, so that nothing in the
/// executable writes the built-in files.</summary>
/// <param name="Path">The file.</param>
/// <param name="Bytes">The file's new bytes, or <c>null</c> when the file is deleted.</param>
internal sealed record CensusFileEdit(string Path, byte[]? Bytes);

/// <summary>What writing a census does to the built-in files.</summary>
/// <param name="Record">The census record.</param>
/// <param name="Problems">What the writer leaves as it was, in words.</param>
/// <param name="Bytes">The bytes of the frameworks' census files once the edits are made.</param>
/// <param name="Edits">The changes to the built-in files, one per changed file.</param>
internal sealed record CensusWrite(CensusRecord Record, IReadOnlyList<string> Problems, long Bytes, IReadOnlyList<CensusFileEdit> Edits);

/// <summary>The owner's regeneration of the shared frameworks' census (ADR 0012 and ADR 0022, amendments of 2026-11-07): reading what the census
/// runs generated, writing it into the built-in files by the regeneration's rules, and comparing two census records.</summary>
internal static class FrameworkCensus
{
    /// <summary>The outcome of a member the regeneration gave a model.</summary>
    internal const string MODEL = "model";

    /// <summary>What ships for a member the regeneration gave a model: that model.</summary>
    internal const string SHIPS_MODEL = "model";

    /// <summary>What ships for a member the regeneration refused that already had a model: the earlier model, whoever wrote it.</summary>
    internal const string SHIPS_KEPT = "kept";

    /// <summary>What ships for a member the regeneration refused that had no model: its id on the failed list.</summary>
    internal const string SHIPS_FAILED = "failed";

    /// <summary>The outcome of a model that does not apply to the member it names in the frameworks' own compilation.</summary>
    internal const string NOT_APPLICABLE = "not-applicable";

    private const string GENERATED = ".concurrency-hunter/models/generated";
    private const string MEMBERS = "members";
    private const string MODELS = "models";
    private const string FAILED = "failed";
    private const string MEMBER = "member";
    private const string ASSEMBLIES = "assemblies";
    private const string VERSIONS = "versions";
    private const string MINIMUM = "minimum";
    private const string MAXIMUM = "maximumExclusive";
    private const string CENSUS_MINIMUM = "10.0.0.0";
    private const string CENSUS_MAXIMUM = "10.0.0.1";
    private const int HASH_DIGITS = 12;

    /// <summary>How a built-in file is written: indented by two spaces with the default encoder, as the topical files are, so a file read
    /// and written back unchanged keeps its bytes.</summary>
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 2 };

    /// <summary>The answers the census runs wrote under their scratch folders, for every member of the two frameworks' assemblies of the
    /// census's major, other than a member of a type a recognizer owns. Each member is found by the id the generator wrote and named by
    /// the id the run looks it up by: in the frameworks' reference assemblies, which a program's call binds to, and in their implementation,
    /// which a generation's call binds to, where Roslyn names it otherwise or no reference assembly declares it. A model that does not apply
    /// to that member is a refusal (<see cref="NOT_APPLICABLE"/>). When two runs answer one member, a model wins over a refusal, and of two
    /// models the first stays; two that differ are a problem.</summary>
    /// <param name="censusFolders">The scratch folders of the census runs, in the order they ran.</param>
    /// <param name="frameworks">The frameworks' assemblies.</param>
    internal static CensusHarvest Harvest(IReadOnlyList<string> censusFolders, FrameworkAssemblies frameworks)
    {
        CSharpCompilation[] compilations =
        [
            CSharpCompilation.Create("FrameworkCensusReference", references: frameworks.ReferenceFiles.Select(file => MetadataReference.CreateFromFile(file))),
            CSharpCompilation.Create("FrameworkCensusImplementation", references: frameworks.Files.Select(file => MetadataReference.CreateFromFile(file)))
        ];
        var answers = new SortedDictionary<string, CensusAnswer>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var folder in censusFolders)
        {
            WorkCancellation.Check();
            foreach (var framework in FrameworkAssemblies.FRAMEWORKS)
            {
                WorkCancellation.Check();
                var directory = Path.Combine(folder, GENERATED, framework, $"{FrameworkAssemblies.MAJOR}.0");
                if (!Directory.Exists(directory))
                    continue;
                foreach (var path in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
                {
                    WorkCancellation.Check();
                    var assembly = Path.GetFileNameWithoutExtension(path);
                    var bytes = File.ReadAllBytes(path);
                    var key = GeneratedFileKey.ForFramework(assembly, new Version(FrameworkAssemblies.MAJOR, 0, 0, 0), framework, FrameworkAssemblies.MAJOR);
                    var file = GeneratedModelFiles.Valid(key, path, bytes, anyGenerator: true);
                    // An unreadable file's diagnostics already name it and why.
                    problems.AddRange(file.Diagnostics);
                    if (file.UnreadableReason is not null)
                        continue;
                    var raw = RawModels(bytes);
                    foreach (var entry in file.Entries.Values.OrderBy(entry => entry.Member, StringComparer.Ordinal))
                    {
                        WorkCancellation.Check();
                        foreach (var answer in Answers(entry, raw, framework, assembly, compilations, problems))
                        {
                            WorkCancellation.Check();
                            Merge(answers, answer, problems);
                        }
                    }
                }
            }
        }

        return new CensusHarvest(answers.Values.ToArray(), problems);
    }

    /// <summary>Plans the writing of a census into the built-in files: its record and the edits that make it. A model replaces the member's census entry wherever it
    /// stood, a topical file's included, and is written to the file of its assembly in its framework's folder. A refused member keeps the
    /// model it already had, whoever wrote it; one without goes to the failed list of its assembly's file. The failed lists are written
    /// anew from this census, so a member that left the census leaves them and keeps its model. A model whose member another built-in
    /// entry describes, one the census does not own, is not written and stays a problem. A framework file left with neither models nor failed
    /// members is deleted.</summary>
    /// <param name="answers">The regeneration's answers.</param>
    /// <param name="builtInFolder">The built-in folder: the topical files at its top, a framework's census files in a folder of its own.</param>
    /// <param name="frameworks">The frameworks' assemblies.</param>
    /// <param name="commit">The commit the generator was built from.</param>
    /// <param name="engine">The engine fingerprint.</param>
    internal static CensusWrite Plan(IReadOnlyList<CensusAnswer> answers, string builtInFolder, FrameworkAssemblies frameworks, string commit,
                                     string engine)
    {
        var files = new BuiltInFiles(Directory.GetFiles(builtInFolder, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                                              .Select(path => BuiltInFile.Read(path, IsFrameworkFile(builtInFolder, path))));
        var problems = new List<string>();
        var rows = new List<CensusRow>();
        foreach (var file in files.All.Where(file => file.IsFramework))
        {
            WorkCancellation.Check();
            file.Failed.Clear();
        }

        foreach (var answer in answers)
        {
            WorkCancellation.Check();
            var described = files.Describing(answer.LookupId);
            var owned = described.Where(item => frameworks.IsCensusEntry(item.File.Record(item.Entry))).ToArray();
            if (answer.Model is { } model && owned.Length != described.Length)
            {
                problems.Add($"{answer.LookupId}: a built-in entry the census does not own describes it, so its model is not written.");
                rows.Add(Row(answer, SHIPS_KEPT, described[0].Entry));
            }
            else if (answer.Model is { } replacement)
            {
                foreach (var item in owned)
                {
                    WorkCancellation.Check();
                    files.Withdraw(item);
                }
                files.Place(FrameworkFile(files, builtInFolder, answer), replacement);
                rows.Add(Row(answer, SHIPS_MODEL, replacement));
            }
            else if (described.Length != 0)
            {
                rows.Add(Row(answer, SHIPS_KEPT, described[0].Entry));
            }
            else
            {
                FrameworkFile(files, builtInFolder, answer).Failed.Add(answer.LookupId);
                rows.Add(Row(answer, SHIPS_FAILED, null));
            }
        }

        var edits = new List<CensusFileEdit>();
        var bytes = 0L;
        foreach (var file in files.All)
        {
            WorkCancellation.Check();
            var edit = file.Edit();
            if (edit is not null)
                edits.Add(edit);
            if (file.IsFramework)
                bytes += edit is null ? file.Length : edit.Bytes?.Length ?? 0;
        }
        return new CensusWrite(new CensusRecord(commit, engine, frameworks.Patches, rows), problems, bytes, edits);
    }

    /// <summary>What differs between two census records, in report lines; empty when they are the same. The frameworks' patches come first,
    /// since another patch explains differences the generator did not make.</summary>
    /// <param name="before">The previous record, or <c>null</c> for a first census.</param>
    /// <param name="after">The new record.</param>
    /// <param name="examples">How many members each line names at most.</param>
    internal static IReadOnlyList<string> Compare(CensusRecord? before, CensusRecord after, int examples)
    {
        var report = new List<string>();
        var earlier = (before?.Rows ?? []).ToDictionary(row => row.LookupId, StringComparer.Ordinal);
        var later = after.Rows.ToDictionary(row => row.LookupId, StringComparer.Ordinal);
        foreach (var framework in FrameworkAssemblies.FRAMEWORKS)
        {
            WorkCancellation.Check();
            var (was, now) = (before?.Patches.GetValueOrDefault(framework), after.Patches.GetValueOrDefault(framework));
            if (was != now)
                report.Add($"{framework}: {was ?? "-"} -> {now ?? "-"}");
        }

        AddMembers(report, "joined the census", later.Keys.Where(member => !earlier.ContainsKey(member)), examples);
        AddMembers(report, "left the census", earlier.Keys.Where(member => !later.ContainsKey(member)), examples);
        AddMembers(report, "models gained", later.Values.Where(row => row.Ships == SHIPS_MODEL &&
                                                                      earlier.GetValueOrDefault(row.LookupId) is not { Ships: SHIPS_MODEL or SHIPS_KEPT })
                                                        .Select(row => row.LookupId), examples);
        AddMembers(report, "models changed", later.Values.Where(row => row.Ships != SHIPS_FAILED &&
                                                                       earlier.GetValueOrDefault(row.LookupId) is { Ships: not SHIPS_FAILED } old &&
                                                                       old.ModelHash != row.ModelHash)
                                                         .Select(row => row.LookupId), examples);
        AddMembers(report, "models kept over a refusal", later.Values.Where(row => row.Ships == SHIPS_KEPT).Select(row => row.LookupId), examples);
        AddMembers(report, "newly failed", later.Values.Where(row => row.Ships == SHIPS_FAILED &&
                                                                     earlier.GetValueOrDefault(row.LookupId) is not { Ships: SHIPS_FAILED })
                                                       .Select(row => row.LookupId), examples);
        AddMembers(report, "failed members that got a model", later.Values.Where(row => row.Ships != SHIPS_FAILED &&
                                                                                        earlier.GetValueOrDefault(row.LookupId) is { Ships: SHIPS_FAILED })
                                                                          .Select(row => row.LookupId), examples);
        var outcomes = (before?.Rows ?? []).Select(row => row.Outcome).Concat(after.Rows.Select(row => row.Outcome)).Distinct(StringComparer.Ordinal)
                                         .Order(StringComparer.Ordinal);
        foreach (var outcome in outcomes)
        {
            WorkCancellation.Check();
            var (was, now) = (earlier.Values.Count(row => row.Outcome == outcome), later.Values.Count(row => row.Outcome == outcome));
            if (was != now)
                report.Add($"outcome {outcome}: {was} -> {now}");
        }

        return report;
    }

    /// <summary>The model objects of a generated file's entries by member, as the file spells them.</summary>
    /// <param name="bytes">The file.</param>
    private static Dictionary<string, JsonObject> RawModels(byte[] bytes)
    {
        var models = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var root = JsonNode.Parse(bytes)!.AsObject();
        foreach (var entry in root[MEMBERS]!.AsArray().OfType<JsonObject>())
        {
            WorkCancellation.Check();
            if (entry["model"] is JsonObject model && entry[MEMBER]?.GetValue<string>() is { } member)
                models.TryAdd(member, model);
        }

        return models;
    }

    /// <summary>The answers one valid entry gives: none when it is no census member's, a member of a type a recognizer owns, or an entry no
    /// member of the assembly answers to, which is a problem; else one under the id the member is found by first and one under each other id
    /// the compilations name it by. Roslyn names a generic type's nested type in a signature as the assembly writes it: constructed in a
    /// reference assembly, <c>~System.Buffers.ReadOnlySequence{`0}.Enumerator</c>, and as a definition in the implementation a generation's
    /// decompiled code binds to, <c>~System.Buffers.ReadOnlySequence`1.Enumerator`0</c>, so each call finds the member by its own id.</summary>
    /// <param name="entry">The valid entry.</param>
    /// <param name="raw">The file's model objects by member.</param>
    /// <param name="framework">The file's framework.</param>
    /// <param name="assembly">The file's assembly.</param>
    /// <param name="compilations">The frameworks' reference compilation, then their implementation compilation.</param>
    /// <param name="problems">Where a problem is added.</param>
    private static CensusAnswer[] Answers(GeneratedModelEntry entry, Dictionary<string, JsonObject> raw, string framework, string assembly,
                                          IReadOnlyList<CSharpCompilation> compilations, List<string> problems)
    {
        if (LibraryModels.IsRecognizedType(LibraryModels.TypeOf(entry.Member)))
            return [];
        var found = compilations.Select(candidate => (Member: Single(entry.Member, assembly, candidate), Compilation: candidate))
                                .Where(item => item.Member is not null).ToArray();
        if (found.Length == 0 || DocumentationCommentId.CreateDeclarationId(found[0].Member!) is not { } lookupId)
        {
            problems.Add($"{entry.Member}: no single member of {assembly} answers to the id.");
            return [];
        }
        string[] ids = [lookupId, .. found.Skip(1).Select(item => DocumentationCommentId.CreateDeclarationId(item.Member!)).OfType<string>()
                                          .Where(id => id != lookupId).Distinct(StringComparer.Ordinal)];

        if (entry.Refusal is { } refusal)
            return [.. ids.Select(id => new CensusAnswer(id, framework, assembly, refusal.Reason, refusal.Causes.Order(StringComparer.Ordinal).ToArray(), null))];
        if (!GeneratedModelFiles.Applies(entry, found[0].Member!, found[0].Compilation, out var diagnostic))
        {
            problems.Add(diagnostic!);
            return [.. ids.Select(id => new CensusAnswer(id, framework, assembly, NOT_APPLICABLE, [], null))];
        }

        return [.. ids.Select(id =>
        {
            var model = raw[entry.Member].DeepClone().AsObject();
            model[MEMBER] = id;
            return new CensusAnswer(id, framework, assembly, MODEL, [], model);
        })];
    }

    /// <summary>The one method of an assembly a declaration id names in a compilation, or <c>null</c> when it names none or several.</summary>
    /// <param name="memberId">The id the generator wrote.</param>
    /// <param name="assembly">The assembly's name.</param>
    /// <param name="compilation">The compilation.</param>
    private static IMethodSymbol? Single(string memberId, string assembly, CSharpCompilation compilation) =>
        DocumentationCommentId.GetSymbolsForDeclarationId(memberId, compilation).OfType<IMethodSymbol>()
                              .Where(symbol => symbol.ContainingAssembly.Name == assembly).Take(2).ToArray() is [{ } member] ? member : null;

    /// <summary>Adds an answer to those of earlier runs: a model wins over a refusal, and of two models or two refusals the first stays; two
    /// models that differ, or two refusals for different reasons, are a problem.</summary>
    /// <param name="answers">The answers so far.</param>
    /// <param name="answer">The new answer.</param>
    /// <param name="problems">Where a problem is added.</param>
    private static void Merge(SortedDictionary<string, CensusAnswer> answers, CensusAnswer answer, List<string> problems)
    {
        if (!answers.TryGetValue(answer.LookupId, out var earlier))
            answers.Add(answer.LookupId, answer);
        else if (earlier.Model is null && answer.Model is not null)
            answers[answer.LookupId] = answer;
        else if (earlier.Model is { } first && answer.Model is { } second && first.ToJsonString() != second.ToJsonString())
            problems.Add($"{answer.LookupId}: two runs gave it different models; the first stays.");
        else if (earlier.Model is null && answer.Model is null && earlier.Outcome != answer.Outcome)
            problems.Add($"{answer.LookupId}: two runs refused it for different reasons, {earlier.Outcome} and {answer.Outcome}; the first stays.");
    }

    /// <summary>The census file of an answer's assembly in its framework's folder, made empty when there is none yet.</summary>
    /// <param name="files">The built-in files.</param>
    /// <param name="builtInFolder">The built-in folder.</param>
    /// <param name="answer">The answer.</param>
    private static BuiltInFile FrameworkFile(BuiltInFiles files, string builtInFolder, CensusAnswer answer)
    {
        var path = Path.Combine(builtInFolder, answer.Framework, answer.Assembly + ".json");
        if (files.Find(path) is { } found)
            return found;
        var created = BuiltInFile.New(path, answer.Framework, answer.Assembly);
        files.Append(created);
        return created;
    }

    /// <summary>One model entry of a built-in file.</summary>
    /// <param name="File">The file the entry stands in.</param>
    /// <param name="Entry">The entry, as the file holds it.</param>
    private sealed record BuiltInModel(BuiltInFile File, JsonObject Entry);

    /// <summary>The built-in files of a census in file order, with two indexes kept current as entries leave and join: the files by path,
    /// compared without regard to case, and the model entries by the member they name, each member's in file order and then entry order.</summary>
    private sealed class BuiltInFiles
    {
        private readonly Dictionary<string, BuiltInFile> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<BuiltInModel>> _byMember = new(StringComparer.Ordinal);

        /// <summary>Indexes the files that were read.</summary>
        /// <param name="read">The files, in file order.</param>
        internal BuiltInFiles(IEnumerable<BuiltInFile> read)
        {
            foreach (var file in read)
            {
                WorkCancellation.Check();
                Append(file);
            }
        }

        /// <summary>The files, in file order.</summary>
        internal List<BuiltInFile> All { get; } = [];

        /// <summary>The file at a path, compared without regard to case, or <c>null</c> when there is none.</summary>
        /// <param name="path">The path.</param>
        internal BuiltInFile? Find(string path) => _byPath.GetValueOrDefault(path);

        /// <summary>Adds a file after the others and indexes its entries.</summary>
        /// <param name="file">The file.</param>
        internal void Append(BuiltInFile file)
        {
            file.Order = All.Count;
            All.Add(file);
            _byPath.TryAdd(file.Path, file);
            foreach (var entry in file.Entries)
            {
                WorkCancellation.Check();
                if (BuiltInFile.MemberOf(entry) is { } member)
                    Of(member).Add(new BuiltInModel(file, entry));
            }
        }

        /// <summary>The entries that name a member now, in file order and then entry order. The list is a copy, so the entries can be
        /// withdrawn while it is read.</summary>
        /// <param name="lookupId">The member's id.</param>
        internal BuiltInModel[] Describing(string lookupId) => _byMember.TryGetValue(lookupId, out var list) ? list.ToArray() : [];

        /// <summary>Takes an entry out of its file and the index.</summary>
        /// <param name="item">The entry.</param>
        internal void Withdraw(BuiltInModel item)
        {
            WorkCancellation.Check();
            item.File.Remove(item.Entry);
            if (BuiltInFile.MemberOf(item.Entry) is { } member)
                Of(member).Remove(item);
        }

        /// <summary>Adds a model to a file and the index, behind the member's entries of the files before it and of the file itself.</summary>
        /// <param name="file">The file.</param>
        /// <param name="model">The model entry.</param>
        internal void Place(BuiltInFile file, JsonObject model)
        {
            WorkCancellation.Check();
            var added = file.Add(model);
            if (BuiltInFile.MemberOf(added) is not { } member)
                return;
            var list = Of(member);
            var position = list.Count;
            while (position != 0 && list[position - 1].File.Order > file.Order)
            {
                WorkCancellation.Check();
                position--;
            }
            list.Insert(position, new BuiltInModel(file, added));
        }

        /// <summary>The index's list for a member, made empty when there is none yet.</summary>
        /// <param name="member">The member's id.</param>
        private List<BuiltInModel> Of(string member)
        {
            if (!_byMember.TryGetValue(member, out var list))
                _byMember.Add(member, list = []);
            return list;
        }
    }

    /// <summary>Whether a built-in file is one of a framework's census files: it lies in a folder below the built-in folder.</summary>
    /// <param name="builtInFolder">The built-in folder.</param>
    /// <param name="path">The file.</param>
    private static bool IsFrameworkFile(string builtInFolder, string path) =>
        !string.Equals(Path.GetFullPath(Path.GetDirectoryName(path)!), Path.GetFullPath(builtInFolder).TrimEnd(Path.DirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);

    /// <summary>The census row of an answer.</summary>
    /// <param name="answer">The answer.</param>
    /// <param name="ships">What ships for the member.</param>
    /// <param name="model">The model that ships, or <c>null</c> for a failed member.</param>
    private static CensusRow Row(CensusAnswer answer, string ships, JsonObject? model) =>
        new(answer.LookupId, answer.Framework, answer.Assembly, answer.Outcome, answer.Causes, ships,
            model is null ? string.Empty : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(model.ToJsonString())))[..HASH_DIGITS]);

    /// <summary>Adds one line for a set of members, when there are any: their count and the first of them.</summary>
    /// <param name="report">The report.</param>
    /// <param name="label">What the members have in common.</param>
    /// <param name="members">The members.</param>
    /// <param name="examples">How many members the line names at most.</param>
    private static void AddMembers(List<string> report, string label, IEnumerable<string> members, int examples)
    {
        var all = members.Order(StringComparer.Ordinal).ToArray();
        if (all.Length != 0)
            report.Add($"{label}: {all.Length}, e.g. {string.Join(", ", all.Take(examples))}");
    }

    /// <summary>One built-in file as the census writer edits it: its JSON, its models by member and, for a framework file, its failed list.
    /// It is written back only when it changed. A topical file, written by hand and escaped as its owner left it, only ever loses entries,
    /// and loses exactly their bytes (<see cref="WithoutModels"/>); a framework file, which only the writer writes, is written whole.</summary>
    private sealed class BuiltInFile
    {
        private readonly JsonObject _root;
        private readonly byte[] _bytes;
        private readonly string _original;
        private readonly string _newLine;
        private readonly JsonObject[] _read;
        private readonly HashSet<JsonObject> _removed = new(ReferenceEqualityComparer.Instance);
        private bool _edited;

        private BuiltInFile(string path, bool isFramework, JsonObject root, byte[] bytes, string newLine)
        {
            Path = path;
            IsFramework = isFramework;
            _root = root;
            _bytes = bytes;
            _original = Encoding.UTF8.GetString(bytes);
            _newLine = newLine;
            _read = Models.OfType<JsonObject>().ToArray();
            Failed = new SortedSet<string>((root[FAILED] as JsonArray ?? []).Select(item => item!.GetValue<string>()), StringComparer.Ordinal);
        }

        /// <summary>The file's path.</summary>
        internal string Path { get; }

        /// <summary>Whether the file is a framework's census file.</summary>
        internal bool IsFramework { get; }

        /// <summary>The failed list: the members of the census without a model.</summary>
        internal SortedSet<string> Failed { get; }

        private JsonArray Models => _root[MODELS]!.AsArray();

        /// <summary>Reads a file.</summary>
        /// <param name="path">The file.</param>
        /// <param name="isFramework">Whether it is a framework's census file.</param>
        internal static BuiltInFile Read(string path, bool isFramework)
        {
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.UTF8.GetString(bytes);
            var root = JsonNode.Parse(text.TrimStart('﻿'))!.AsObject();
            return new BuiltInFile(path, isFramework, root, bytes, text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
        }

        /// <summary>A framework's census file for one assembly that does not exist yet.</summary>
        /// <param name="path">The file.</param>
        /// <param name="framework">The framework.</param>
        /// <param name="assembly">The assembly.</param>
        internal static BuiltInFile New(string path, string framework, string assembly)
        {
            var root = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["note"] = $"The census of {framework} {FrameworkAssemblies.MAJOR}.0 in {assembly}: the models and the failed members of the owner's " +
                           "regeneration (ADR 0022). Written by build/generate-frameworks.ps1; an entry edited here is replaced by the next regeneration " +
                           "when the generator gives the member a model.",
                [ASSEMBLIES] = new JsonArray(assembly),
                [VERSIONS] = new JsonObject { [MINIMUM] = CENSUS_MINIMUM, [MAXIMUM] = CENSUS_MAXIMUM },
                [MODELS] = new JsonArray()
            };
            return new BuiltInFile(path, true, root, [], "\n");
        }

        /// <summary>The file's position among the built-in files.</summary>
        internal int Order { get; set; }

        /// <summary>The file's model entries now, in order.</summary>
        internal IEnumerable<JsonObject> Entries => Models.OfType<JsonObject>();

        /// <summary>The member an entry names, or <c>null</c> when it names none.</summary>
        /// <param name="entry">The entry.</param>
        internal static string? MemberOf(JsonObject entry) => entry[MEMBER]?.GetValue<string>();

        /// <summary>The library model record an entry of this file stands for, with the file's assemblies and range where the entry names none.</summary>
        /// <param name="entry">The entry.</param>
        internal LibraryModel Record(JsonObject entry)
        {
            var assemblies = (entry[ASSEMBLIES] ?? _root[ASSEMBLIES])!.AsArray().Select(item => item!.GetValue<string>());
            var versions = (entry[VERSIONS] ?? _root[VERSIONS])!.AsObject();
            var (minimum, maximum) = (Version.Parse(versions[MINIMUM]!.GetValue<string>()), Version.Parse(versions[MAXIMUM]!.GetValue<string>()));
            return new LibraryModel(entry[MEMBER]!.GetValue<string>(), assemblies.Select(name => new SupportedAssemblyVersion(name, minimum, maximum)).ToArray(), []);
        }

        /// <summary>Takes an entry out of the file.</summary>
        /// <param name="entry">The entry.</param>
        internal void Remove(JsonObject entry)
        {
            WorkCancellation.Check();
            if (!Models.Remove(entry))
                return;
            _removed.Add(entry);
            _edited = true;
        }

        /// <summary>Adds a model to the file, after any entry of the same member is taken out.</summary>
        /// <param name="model">The model entry.</param>
        /// <returns>The copy of the entry the file holds.</returns>
        internal JsonObject Add(JsonObject model)
        {
            WorkCancellation.Check();
            var added = model.DeepClone().AsObject();
            Models.Add(added);
            _edited = true;
            return added;
        }

        /// <summary>The bytes the file has now.</summary>
        internal long Length => _bytes.Length;

        /// <summary>The edit that writes the file back, or <c>null</c> when it is unchanged: a topical file only when an entry left it, a
        /// framework file with its models and failed list sorted by member, deleted when it holds neither.</summary>
        internal CensusFileEdit? Edit()
        {
            if (!IsFramework)
            {
                return _edited
                    ? new CensusFileEdit(Path, WithoutModels(_bytes, _read.Index().Where(item => _removed.Contains(item.Item)).Select(item => item.Index).ToHashSet()))
                    : null;
            }

            var sorted = Models.OfType<JsonObject>().OrderBy(entry => entry[MEMBER]!.GetValue<string>(), StringComparer.Ordinal)
                               .Select(entry => entry.DeepClone()).ToArray();
            Models.Clear();
            foreach (var entry in sorted)
            {
                WorkCancellation.Check();
                Models.Add(entry);
            }
            _root.Remove(FAILED);
            if (Failed.Count != 0)
                _root[FAILED] = new JsonArray(Failed.Select(member => (JsonNode)member).ToArray());
            if (Models.Count == 0 && Failed.Count == 0)
                return _bytes.Length == 0 ? null : new CensusFileEdit(Path, null);

            var text = Format(_root, _newLine, _original.EndsWith('\n') || _original.Length == 0);
            return text == _original ? null : new CensusFileEdit(Path, new UTF8Encoding(false).GetBytes(text));
        }
    }

    /// <summary>A built-in file's text: indented by two spaces with the default encoder, with the line ending given and a final one when
    /// asked.</summary>
    /// <param name="root">The file's JSON.</param>
    /// <param name="newLine">The line ending.</param>
    /// <param name="finalNewLine">Whether the text ends with a line ending.</param>
    internal static string Format(JsonNode root, string newLine, bool finalNewLine)
    {
        var text = root.ToJsonString(new JsonSerializerOptions(Indented) { NewLine = newLine });
        return finalNewLine ? text + newLine : text;
    }

    /// <summary>A built-in file without some elements of its <c>models</c> array, every other byte as it was: what each kept element is,
    /// how it is escaped and indented, and what lies before and after the array. The kept elements are joined by the separator that stood
    /// between the file's first two; with none kept the array is <c>[]</c>. A hand-written file escapes its text as its owner left it, so a
    /// serializer would rewrite all of it.</summary>
    /// <param name="original">The file's bytes, with or without a byte order mark.</param>
    /// <param name="removed">The positions in the array of the elements to take out.</param>
    internal static byte[] WithoutModels(byte[] original, IReadOnlySet<int> removed)
    {
        if (removed.Count == 0)
            return original;
        var offset = original.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        var reader = new Utf8JsonReader(original.AsSpan(offset));
        var elements = new List<ByteSpan>();
        var (open, close) = (-1L, -1L);
        reader.Read();
        while (open < 0 && reader.Read() && reader.CurrentDepth >= 1)
        {
            WorkCancellation.Check();
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                continue;
            var models = reader.ValueTextEquals(MODELS);
            reader.Read();
            if (!models)
            {
                reader.Skip();
                continue;
            }
            open = reader.TokenStartIndex;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                WorkCancellation.Check();
                var start = reader.TokenStartIndex;
                reader.Skip();
                elements.Add(new ByteSpan(start + offset, reader.BytesConsumed + offset));
            }
            close = reader.TokenStartIndex;
        }
        if (open < 0 || removed.Any(index => index < 0 || index >= elements.Count))
            throw new FormatException("The file has no models array holding the elements to take out.");

        var kept = elements.Where((_, index) => !removed.Contains(index)).ToArray();
        var result = new List<byte>(original.Length);
        result.AddRange(original.AsSpan(0, (int)(open + offset + 1)));
        if (kept.Length != 0)
        {
            var separator = Slice(original, elements[0].End, elements[1].Start);
            result.AddRange(Slice(original, open + offset + 1, elements[0].Start));
            for (var index = 0; index < kept.Length; index++)
            {
                WorkCancellation.Check();
                if (index != 0)
                    result.AddRange(separator);
                result.AddRange(Slice(original, kept[index].Start, kept[index].End));
            }
            result.AddRange(Slice(original, elements[^1].End, close + offset));
        }
        result.AddRange(original.AsSpan((int)(close + offset)));
        return result.ToArray();
    }

    /// <summary>The bytes of a file from one position up to another.</summary>
    /// <param name="bytes">The file.</param>
    /// <param name="start">The first position.</param>
    /// <param name="end">The position after the last.</param>
    private static byte[] Slice(byte[] bytes, long start, long end) => bytes[(int)start..(int)end];

    /// <summary>Where one element of a file's array lies.</summary>
    /// <param name="Start">The position of its first byte.</param>
    /// <param name="End">The position after its last byte.</param>
    private readonly record struct ByteSpan(long Start, long End);
}
