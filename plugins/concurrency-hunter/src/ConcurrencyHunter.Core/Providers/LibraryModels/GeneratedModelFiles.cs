using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers.LibraryModels;

/// <summary>Which generated file answers for an assembly (R2): a package's assembly by the package's id, its version and the MVID of the
/// asset the target framework selects, or an assembly of a shared framework by the framework's name and major, whatever patch is
/// installed. A target whose implementation is not on disk keeps the part of its key its reference names: a package's id and version, with
/// the MVID of its asset when the asset is on disk and none when it is missing, a framework's name, major and assembly name.</summary>
internal sealed record GeneratedFileKey
{
    /// <summary>The assembly's name.</summary>
    internal string AssemblyName { get; private init; } = "";

    /// <summary>The assembly's version, four parts.</summary>
    internal Version AssemblyVersion { get; private init; } = new(0, 0, 0, 0);

    /// <summary>The package's id, or <c>null</c> for an assembly of a shared framework.</summary>
    internal string? Package { get; private init; }

    /// <summary>The package's version, or <c>null</c> for an assembly of a shared framework.</summary>
    internal string? PackageVersion { get; private init; }

    /// <summary>The MVID of the asset a package's target framework selects, or <c>null</c> when the asset is missing or the assembly
    /// belongs to a shared framework.</summary>
    internal Guid? Mvid { get; private init; }

    /// <summary>The shared framework's name, or <c>null</c> for a package's assembly.</summary>
    internal string? Framework { get; private init; }

    /// <summary>The shared framework's major version, or <c>null</c> for a package's assembly.</summary>
    internal int? Major { get; private init; }

    /// <summary>Whether the key is a package's assembly.</summary>
    internal bool IsPackage => Package is not null;

    /// <summary>The file's path below <c>.concurrency-hunter/models/generated/</c>, with <c>/</c>, or <c>null</c> when a package's asset is
    /// missing and so no file can exist.</summary>
    internal string? RelativePath => IsPackage
        ? Mvid is { } mvid ? $"{Package}/{PackageVersion}/{mvid:D}.json" : null
        : $"{Framework}/{Major}.0/{AssemblyName}.json";

    /// <summary>The key as coverage names it: the file's path below <c>.concurrency-hunter/models/generated/</c>, or, for a package whose
    /// asset is missing and so has no file, the package's id and version alone.</summary>
    internal string Text => RelativePath ?? $"{Package}/{PackageVersion}";

    /// <summary>The key of a package's assembly.</summary>
    /// <param name="assemblyName">The assembly's name.</param>
    /// <param name="assemblyVersion">The assembly's version.</param>
    /// <param name="package">The package's id.</param>
    /// <param name="packageVersion">The package's version.</param>
    /// <param name="mvid">The MVID of the asset, or <c>null</c> when the asset is missing.</param>
    internal static GeneratedFileKey ForPackage(string assemblyName, Version assemblyVersion, string package, string packageVersion,
                                                Guid? mvid) =>
        new()
        {
            AssemblyName = assemblyName,
            AssemblyVersion = FourParts(assemblyVersion),
            Package = Segment(package),
            PackageVersion = Segment(packageVersion),
            Mvid = mvid
        };

    /// <summary>The key of an assembly of a shared framework.</summary>
    /// <param name="assemblyName">The assembly's name.</param>
    /// <param name="assemblyVersion">The assembly's version.</param>
    /// <param name="framework">The framework's name.</param>
    /// <param name="major">The framework's major version.</param>
    internal static GeneratedFileKey ForFramework(string assemblyName, Version assemblyVersion, string framework, int major) =>
        new()
        {
            AssemblyName = Segment(assemblyName),
            AssemblyVersion = FourParts(assemblyVersion),
            Framework = Segment(framework),
            Major = major >= 0 ? major : throw new ArgumentOutOfRangeException(nameof(major), major, "A major version is not negative.")
        };

    /// <summary>The file's full path under a repository, or <c>null</c> when no file can exist.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    internal string? PathUnder(string repositoryRoot) =>
        RelativePath is { } relative
            ? Path.GetFullPath(Path.Combine(repositoryRoot, ".concurrency-hunter", "models", "generated", relative))
            : null;

    private static Version FourParts(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    /// <summary>A key part that becomes a folder or file name: one name, never a path.</summary>
    /// <param name="value">The part.</param>
    private static string Segment(string value) =>
        value.Length == 0 || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            ? throw new ArgumentException($"'{value}' cannot name a folder or file.", nameof(value))
            : value;
}

/// <summary>What happened to one member's generation, as far as R3's recording rule needs to tell.</summary>
internal enum GenerationOutcomeKind
{
    /// <summary>The generator gave a model.</summary>
    Model,

    /// <summary>The generator refused with a reason word of <see cref="GenerationReasons"/> or <see cref="ModelReasons"/>.</summary>
    Refusal,

    /// <summary>The member reached its own limit.</summary>
    Timeout,

    /// <summary>The run's limit did not reach the member, or cut it.</summary>
    Budget,

    /// <summary>The run's own cancellation or deadline fired.</summary>
    Cancelled,

    /// <summary>The generation threw.</summary>
    Failed
}

/// <summary>What a generation came to: a model, a refusal with its reason word, <c>timeout</c>, <c>budget</c>, <c>cancelled</c> or
/// <c>failed</c>. <see cref="GeneratedModelFiles.Keeps"/> says which a file records.</summary>
/// <param name="Kind">The kind of outcome.</param>
/// <param name="Reason">The reason word of a refusal, otherwise <c>null</c>.</param>
internal readonly record struct GenerationOutcome(GenerationOutcomeKind Kind, string? Reason = null)
{
    /// <summary>The word of a member that reached its own limit; a file keeps it as a refusal.</summary>
    internal const string TIMEOUT = "timeout";

    /// <summary>The word of a member the run's limit did not reach or cut.</summary>
    internal const string BUDGET = "budget";

    /// <summary>The word of a generation the run's own cancellation or deadline stopped.</summary>
    internal const string CANCELLED = "cancelled";

    /// <summary>The word of a generation that threw.</summary>
    internal const string FAILED = "failed";

    /// <summary>The generator gave a model.</summary>
    internal static GenerationOutcome Model { get; } = new(GenerationOutcomeKind.Model);

    /// <summary>The member reached its own limit.</summary>
    internal static GenerationOutcome Timeout { get; } = new(GenerationOutcomeKind.Timeout);

    /// <summary>The run's limit did not reach the member, or cut it.</summary>
    internal static GenerationOutcome Budget { get; } = new(GenerationOutcomeKind.Budget);

    /// <summary>The run's own cancellation or deadline fired.</summary>
    internal static GenerationOutcome Cancelled { get; } = new(GenerationOutcomeKind.Cancelled);

    /// <summary>The generation threw.</summary>
    internal static GenerationOutcome Failed { get; } = new(GenerationOutcomeKind.Failed);

    /// <summary>The outcome a reason word names: the four words that are no refusal of the generator, otherwise a refusal.</summary>
    /// <param name="word">The reason word, as a file or the generator writes it.</param>
    internal static GenerationOutcome OfReason(string word) => word switch
    {
        TIMEOUT => Timeout,
        BUDGET => Budget,
        CANCELLED => Cancelled,
        FAILED => Failed,
        _ => new GenerationOutcome(GenerationOutcomeKind.Refusal, word)
    };
}

/// <summary>A <b>Generation refusal</b> as a file keeps it: the generator's reason word, its detail and its cause words.</summary>
/// <param name="Reason">The reason word.</param>
/// <param name="Detail">The detail, in words.</param>
/// <param name="Causes">The cause words, in the order the generator names them.</param>
internal sealed record GeneratedRefusal(string Reason, string Detail, IReadOnlyList<string> Causes)
{
    /// <summary>What the refusal comes to for R3's recording rule.</summary>
    internal GenerationOutcome Outcome => GenerationOutcome.OfReason(Reason);

    /// <summary>The word that names the refusal in coverage: its reason word, or, without one, its first cause word.</summary>
    internal string Word => Reason.Length != 0 ? Reason : Causes.FirstOrDefault() ?? Reason;
}

/// <summary>A valid entry of a generated file: a model in the project entry form or a refusal.</summary>
/// <param name="Path">The file's path as a diagnostic writes it.</param>
/// <param name="Member">The member the entry answers: the declaration id the generator writes.</param>
/// <param name="Model">The model as the project reader reads its entry form, or <c>null</c> for a refusal.</param>
/// <param name="Refusal">The refusal, or <c>null</c> for a model.</param>
internal sealed record GeneratedModelEntry(string Path, string Member, ProjectModelEntry? Model, GeneratedRefusal? Refusal);

/// <summary>An answer the run hands to a generated file: the generator's model, or a refusal, for one member.</summary>
internal sealed class GeneratedModelAnswer
{
    private readonly LibraryModel? _model;
    private readonly GeneratedRefusal? _refusal;

    private GeneratedModelAnswer(string member, LibraryModel? model, GeneratedRefusal? refusal)
    {
        Member = member;
        _model = model;
        _refusal = refusal;
    }

    /// <summary>The member answered.</summary>
    internal string Member { get; }

    /// <summary>What the answer comes to for R3's recording rule.</summary>
    internal GenerationOutcome Outcome => _refusal is null ? GenerationOutcome.Model : _refusal.Outcome;

    /// <summary>The refusal, or <c>null</c> for the answer of a member the generator gave a model for.</summary>
    internal GeneratedRefusal? Refusal => _refusal;

    /// <summary>The answer of a member the generator gave a model for.</summary>
    /// <param name="model">The model; its id is the member.</param>
    internal static GeneratedModelAnswer OfModel(LibraryModel model) => new(model.Id, model, null);

    /// <summary>The answer of a member the generator refused, or that ran out of its limit.</summary>
    /// <param name="member">The member's declaration id.</param>
    /// <param name="refusal">The refusal.</param>
    internal static GeneratedModelAnswer OfRefusal(string member, GeneratedRefusal refusal) => new(member, null, refusal);

    /// <summary>Writes the entry as a file keeps it.</summary>
    /// <param name="json">The writer.</param>
    internal void WriteTo(Utf8JsonWriter json)
    {
        WorkCancellation.Check();
        json.WriteStartObject();
        json.WriteString("member", Member);
        if (_model is not null)
        {
            json.WritePropertyName("model");
            ModelEntryWriter.Write(_model).WriteTo(json);
        }
        else
        {
            json.WriteStartObject("refusal");
            json.WriteString("reason", _refusal!.Reason);
            json.WriteString("detail", _refusal.Detail);
            json.WriteStartArray("causes");
            foreach (var cause in _refusal.Causes)
            {
                WorkCancellation.Check();
                json.WriteStringValue(cause);
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndObject();
    }
}

/// <summary>A generated file as one run reads it: the entries that hold, and what was wrong with the rest.</summary>
/// <param name="Key">The key the file answers for.</param>
/// <param name="DisplayPath">The file's path as a diagnostic writes it.</param>
/// <param name="UnreadableReason">Why the file is unreadable, or <c>null</c> when it is readable. An unreadable file counts as empty and is
/// never written in the run.</param>
/// <param name="Entries">The valid entries by member, compared ordinally; empty for an unreadable file.</param>
/// <param name="Diagnostics">One diagnostic for an unreadable file and for each invalid entry.</param>
internal sealed record GeneratedModelFile(GeneratedFileKey Key, string DisplayPath, string? UnreadableReason,
                                          IReadOnlyDictionary<string, GeneratedModelEntry> Entries, IReadOnlyList<string> Diagnostics)
{
    /// <summary>The file's full path, or <c>null</c> when none can exist.</summary>
    internal string? FullPath { get; init; }

    /// <summary>Whether the file is unreadable.</summary>
    internal bool IsUnreadable => UnreadableReason is not null;

    /// <summary>Whether another generator version wrote the file, so its answers are not used.</summary>
    internal bool IsOtherVersion { get; init; }
}

/// <summary>The one owner of the generated model layer's files (R2, R3): where a file lives, what it holds, which entries hold, whether
/// one applies to a compilation, what a file keeps and how a run adds to it.</summary>
internal static class GeneratedModelFiles
{
    private const int SCHEMA_VERSION = 2;
    private const string MUTEX_PREFIX = "ConcurrencyHunter.GeneratedModels.";

    // A property named twice is a fault at any depth of the file, inside a model or a refusal as much as in the header.
    private static readonly JsonDocumentOptions StrictDocument = new() { AllowDuplicateProperties = false };

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // R3: a file keeps nothing that depends on the run or the machine. timeout stands apart from these as its own outcome.
    private static readonly IReadOnlySet<string> KeptReasons = GenerationReasons.Ordered.Concat(ModelReasons.Ordered)
                                                                                 .Except([GenerationReasons.CORELIB, GenerationReasons.NO_IMPLEMENTATION,
                                                                                            GenerationReasons.UNNAMED_MEMBER, GenerationReasons.TIMEOUT], StringComparer.Ordinal)
                                                                                 .ToHashSet(StringComparer.Ordinal);

    /// <summary>R3's recording rule: whether a file keeps a generation's outcome. It keeps a model, the refusals of the generator but
    /// those that depend on the machine (<c>corelib</c>, <c>no-implementation</c>, <c>unnamed-member</c>) and <c>timeout</c>; it keeps
    /// neither <c>budget</c>, <c>cancelled</c> nor <c>failed</c>, nor a word it does not know.</summary>
    /// <param name="outcome">The outcome.</param>
    internal static bool Keeps(GenerationOutcome outcome) => outcome.Kind switch
    {
        GenerationOutcomeKind.Model or GenerationOutcomeKind.Timeout => true,
        GenerationOutcomeKind.Refusal => outcome.Reason is { } reason && KeptReasons.Contains(reason),
        GenerationOutcomeKind.Budget or GenerationOutcomeKind.Cancelled or GenerationOutcomeKind.Failed => false,
        _ => throw new UnreachableException($"Unknown generation outcome {outcome.Kind}.")
    };

    /// <summary>The name of the mutex every writer of a file takes: a hash of the file's full path read as Windows reads it, so the
    /// spellings of one path give one name.</summary>
    /// <param name="path">The file's path.</param>
    internal static string MutexName(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('/', '\\').ToUpperInvariant();
        return MUTEX_PREFIX + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    /// <summary>Reads the file a key names under a repository. A missing file is empty and readable; one that cannot be read is
    /// unreadable.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="key">The key.</param>
    internal static GeneratedModelFile Read(string repositoryRoot, GeneratedFileKey key)
    {
        if (key.PathUnder(repositoryRoot) is not { } path)
            return Empty(key, "");
        var display = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Empty(key, display) with { FullPath = path };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Unreadable(key, display, $"cannot read file: {error.Message}") with { FullPath = path };
        }

        return Valid(key, display, bytes) with { FullPath = path };
    }

    /// <summary>R2's validity, which needs no compilation. A header that does not name the key in every part makes the whole file
    /// unreadable. An entry is valid when its member is its model's member and the model reads back through the project reader's entry
    /// form, its words in the vocabulary and not marked <c>opaque</c>, or when it is a refusal of a reason word a file keeps; two entries
    /// that name one member are both invalid, whatever they hold. An invalid entry is a diagnostic and counts as absent.</summary>
    /// <param name="key">The key the file answers for.</param>
    /// <param name="displayPath">The file's path as a diagnostic writes it.</param>
    /// <param name="bytes">The file.</param>
    internal static GeneratedModelFile Valid(GeneratedFileKey key, string displayPath, byte[] bytes)
    {
        IReadOnlyList<RawEntry> raw;
        bool isOtherVersion;
        try
        {
            raw = Structure(key, bytes, out isOtherVersion);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or FormatException)
        {
            return Unreadable(key, displayPath, error.Message);
        }

        if (isOtherVersion)
            return Empty(key, displayPath) with
            {
                IsOtherVersion = true,
                Diagnostics = [Diagnostic(displayPath, "written by another generator version; its answers are not used.")]
            };

        var counts = raw.GroupBy(entry => entry.Member, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var entries = new Dictionary<string, GeneratedModelEntry>(StringComparer.Ordinal);
        var diagnostics = new List<string>();
        foreach (var entry in raw)
        {
            WorkCancellation.Check();
            GeneratedModelEntry? valid = null;
            var reason = counts[entry.Member] > 1 ? "the file names this member in more than one entry." : Check(displayPath, entry, out valid);
            if (reason is null)
                entries.Add(entry.Member, valid!);
            else
                diagnostics.Add(Diagnostic(displayPath, $"{entry.Member}: {reason}"));
        }

        return new GeneratedModelFile(key, displayPath, null, entries, diagnostics);
    }

    /// <summary>R2's applicability, per compilation: whether a valid entry describes a call of a member in this compilation. A refusal
    /// describes none and applies as it stands; a model applies when it names the member and passes the checks of a project entry that
    /// need the compilation: the declaring assembly, the assembly's version range, the parameter names, the vocabulary against the
    /// member and the modelled calls, and when every member those calls name is one the receiver's type has in this compilation. An entry
    /// is matched to a member by the declaration id of the member's original definition, compared ordinally.</summary>
    /// <param name="entry">The valid entry.</param>
    /// <param name="member">The symbol the call's target resolved to in the compilation.</param>
    /// <param name="compilation">The compilation.</param>
    /// <param name="diagnostic">When the entry does not apply, the diagnostic that names the compilation; otherwise <c>null</c>.</param>
    internal static bool Applies(GeneratedModelEntry entry, IMethodSymbol member, Compilation compilation, out string? diagnostic)
    {
        var definition = (member.ReducedFrom ?? member).OriginalDefinition;
        var reason = !string.Equals(entry.Member, definition.GetDocumentationCommentId(), StringComparison.Ordinal)
            ? "the entry answers another member than the call's."
            : entry.Model is { } model ? ProjectModelResolver.EntryRejection(model, definition, compilation) ?? AbsentCallMember(model, definition, compilation)
                                       : null;
        diagnostic = reason is null ? null : Diagnostic(entry.Path, $"{entry.Member}: does not apply to compilation {compilation.AssemblyName}: {reason}");
        return reason is null;
    }

    /// <summary>The first modelled call of a model whose member the call's compilation does not give the receiver's type, in words, or
    /// <c>null</c> when every one has its member. The receiver's type is what the compilation's dependencies make of the type the model
    /// names, so one entry may fit one project of a scope and not another.</summary>
    /// <param name="model">The model, whose modelled calls the project entry check has accepted.</param>
    /// <param name="definition">The member's original definition.</param>
    /// <param name="compilation">The compilation.</param>
    private static string? AbsentCallMember(ProjectModelEntry model, IMethodSymbol definition, Compilation compilation)
    {
        if (model.Calls.Count == 0)
            return null;
        var receivers = LibraryVocabulary.CallReceivers(model.Calls, definition, compilation);
        for (var index = 0; index < model.Calls.Count; index++)
        {
            WorkCancellation.Check();
            var call = model.Calls[index];
            if (receivers[index] is { } receiver && !HasMember(receiver, call.Member, call.Inputs.Count))
                return $"the call of {call.Member} on {call.On.Canonical} names a member that {receiver.ToDisplayString()} does not have.";
        }

        return null;
    }

    /// <summary>Whether objects of a static type can run a modelled call's member: the type, a base type or an interface it implements
    /// declares an instance method of that name with that many parameters. A type that stands for any object — a type parameter, an
    /// interface or <c>object</c> — has whatever its subtypes have, so it is never short of the member.</summary>
    /// <param name="type">The receiver's type.</param>
    /// <param name="member">The member's name.</param>
    /// <param name="parameters">How many parameters the member takes.</param>
    private static bool HasMember(ITypeSymbol type, string member, int parameters)
    {
        if (type is ITypeParameterSymbol || type.TypeKind == TypeKind.Interface || type.SpecialType == SpecialType.System_Object)
            return true;
        var declaring = new List<ITypeSymbol>();
        for (var current = type; current is not null; current = current.BaseType)
        {
            WorkCancellation.Check();
            declaring.Add(current);
        }
        declaring.AddRange(type.AllInterfaces);
        return declaring.Any(candidate => candidate.GetMembers(member).OfType<IMethodSymbol>()
                                                   .Any(method => !method.IsStatic && method.Parameters.Length == parameters));
    }

    /// <summary>The entry an answer the run generated is once a file has kept it and a later run has read it back: the answer is written as a
    /// file writes it and checked as a file's entry is, so a run applies its own answers exactly as the next run applies the file, also
    /// when the file cannot be written or read.</summary>
    /// <param name="answer">The run's answer.</param>
    /// <param name="displayPath">The file's path as a diagnostic writes it.</param>
    /// <returns>The entry, or <c>null</c> when a file does not keep the answer or the answer does not read back as a valid entry.</returns>
    internal static GeneratedModelEntry? EntryOf(GeneratedModelAnswer answer, string displayPath)
    {
        if (!Keeps(answer.Outcome))
            return null;
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
            answer.WriteTo(json);
        using var document = JsonDocument.Parse(stream.ToArray());
        return Check(displayPath, new RawEntry(answer.Member, document.RootElement), out var entry) is null ? entry : null;
    }

    /// <summary>Adds answers to a file. Under a mutex every writer of the file takes, it re-reads the file, keeps every entry it finds
    /// and adds or replaces only the members these answers name; an answer a file does not keep (<see cref="Keeps"/>) is left out. The
    /// result is written deterministically by atomic replacement, and not at all when its bytes are the file's. An unreadable file is
    /// never written. A write error leaves the previous file whole and is a diagnostic.</summary>
    /// <param name="file">The file as the run read it.</param>
    /// <param name="answers">The answers; the last of a member wins.</param>
    /// <param name="cancellationToken">Stops the wait for the lock: the writer then writes nothing and throws.</param>
    /// <param name="afterRead">A hook called while the lock is held, between the re-read and the replacement; a test holds a writer
    /// there.</param>
    /// <param name="beforeRead">A test hook called under the lock immediately before re-reading the file.</param>
    /// <param name="replaceFile">A test hook replacing the atomic file replacement or move.</param>
    /// <returns>The diagnostics of the write; empty when nothing went wrong.</returns>
    /// <exception cref="OperationCanceledException">The token fired before the writer held the lock, or while it waited for it.</exception>
    internal static IReadOnlyList<string> Write(GeneratedModelFile file, IReadOnlyList<GeneratedModelAnswer> answers,
                                                CancellationToken cancellationToken, Action? afterRead = null, Action? beforeRead = null,
                                                Action<string, string>? replaceFile = null)
    {
        var path = file.FullPath ?? throw new ArgumentException("The file has no path to write.", nameof(file));
        if (file.IsUnreadable)
            return [];
        var kept = new Dictionary<string, GeneratedModelAnswer>(StringComparer.Ordinal);
        foreach (var answer in answers.Where(answer => Keeps(answer.Outcome)))
        {
            CheckCancellation();
            kept[answer.Member] = answer;
        }
        if (kept.Count == 0)
            return [];
        CheckCancellation();

        Mutex mutex;
        try
        {
            mutex = new Mutex(false, MutexName(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return [Diagnostic(file.DisplayPath, $"cannot take the lock: {error.Message}")];
        }

        using (mutex)
        {
            Acquire(mutex, cancellationToken);
            try
            {
                CheckCancellation();
                return WriteLocked(file, path, kept, afterRead, beforeRead, replaceFile);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }

        void CheckCancellation()
        {
            WorkCancellation.Check();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Waits for a mutex, or for the token. A mutex its holder abandoned is taken: the holder ended without releasing it, and
    /// the writer's re-read covers what the holder left.</summary>
    /// <param name="mutex">The mutex.</param>
    /// <param name="cancellationToken">The token.</param>
    private static void Acquire(Mutex mutex, CancellationToken cancellationToken)
    {
        try
        {
            if (WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle]) != 0)
                throw new OperationCanceledException(cancellationToken);
        }
        catch (AbandonedMutexException)
        {
        }
    }

    private static IReadOnlyList<string> WriteLocked(GeneratedModelFile file, string path, Dictionary<string, GeneratedModelAnswer> kept,
                                                     Action? afterRead, Action? beforeRead, Action<string, string>? replaceFile)
    {
        beforeRead?.Invoke();
        byte[]? original;
        try
        {
            original = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            original = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [Diagnostic(file.DisplayPath, $"cannot read file: {error.Message}")];
        }

        IReadOnlyList<RawEntry> existing = [];
        if (original is not null)
        {
            try
            {
                existing = Structure(file.Key, original, out _);
            }
            catch (Exception error) when (error is JsonException or DecoderFallbackException or FormatException)
            {
                return [Diagnostic(file.DisplayPath, error.Message)];
            }
        }

        afterRead?.Invoke();
        var slots = existing.Where(entry => !kept.ContainsKey(entry.Member))
                            .Select(entry => (entry.Member, Write: (Action<Utf8JsonWriter>)(json => entry.Element.WriteTo(json))))
                            .Concat(kept.Values.Select(answer => (answer.Member, Write: (Action<Utf8JsonWriter>)answer.WriteTo)))
                            .OrderBy(slot => slot.Member, StringComparer.Ordinal)
                            .ToArray();
        var bytes = Serialize(file.Key, slots.Select(slot => slot.Write));
        if (original is not null && original.AsSpan().SequenceEqual(bytes))
            return [];

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temporary, bytes);
            // bounded: at most five attempts while holding the mutex, with waits of 50, 100, 200 and 400 milliseconds.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (replaceFile is not null)
                        replaceFile(temporary, path);
                    else if (File.Exists(path))
                        File.Replace(temporary, path, null);
                    else
                        File.Move(temporary, path);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50 << attempt);
                }
            }
            return [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [Diagnostic(file.DisplayPath, $"cannot write file: {error.Message}")];
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static byte[] Serialize(GeneratedFileKey key, IEnumerable<Action<Utf8JsonWriter>> entries)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", SCHEMA_VERSION);
            json.WriteString("generator", GeneratorVersion.Fingerprint);
            json.WriteStartObject("assembly");
            json.WriteString("name", key.AssemblyName);
            json.WriteString("version", key.AssemblyVersion.ToString(4));
            if (key.Mvid is { } mvid)
                json.WriteString("mvid", mvid.ToString("D"));
            else
                json.WriteNull("mvid");
            if (key.IsPackage)
            {
                json.WriteString("package", key.Package);
                json.WriteString("packageVersion", key.PackageVersion);
            }
            else
            {
                json.WriteString("framework", key.Framework);
                json.WriteNumber("major", key.Major!.Value);
            }

            json.WriteEndObject();
            json.WriteStartArray("members");
            foreach (var entry in entries)
            {
                WorkCancellation.Check();
                entry(json);
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <summary>The file's shape and header, which a file must have to be read at all: strict UTF-8 JSON without a repeated property at
    /// any depth and a header that names the key in every part. A file of another generator version has no entries; in the current version,
    /// the file, its header and its entries have no unknown properties and entries are objects with a member. What an entry holds is
    /// <see cref="Check"/>'s.</summary>
    /// <param name="key">The key the file answers for.</param>
    /// <param name="bytes">The file.</param>
    /// <param name="isOtherVersion">Whether the file names another schema or generator version, or names none.</param>
    /// <exception cref="FormatException">The file's shape or header breaks the rule.</exception>
    private static IReadOnlyList<RawEntry> Structure(GeneratedFileKey key, byte[] bytes, out bool isOtherVersion)
    {
        isOtherVersion = false;
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.StartsWith('﻿'))
            text = text[1..];
        using var document = JsonDocument.Parse(text, StrictDocument);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("the file must be a JSON object.");
        if (!root.TryGetProperty("assembly", out var assembly))
            throw new FormatException("the file is missing the property 'assembly'.");
        Header(key, assembly);
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version) || version != SCHEMA_VERSION ||
            !root.TryGetProperty("generator", out var generator) || generator.ValueKind != JsonValueKind.String ||
            !string.Equals(generator.GetString(), GeneratorVersion.Fingerprint, StringComparison.Ordinal))
        {
            isOtherVersion = true;
            return [];
        }

        Properties(root, "the file", ["schemaVersion", "generator", "assembly", "members"], []);
        if (root.GetProperty("members") is not { ValueKind: JsonValueKind.Array } members)
            throw new FormatException("members must be an array.");
        var entries = new List<RawEntry>();
        foreach (var entry in members.EnumerateArray())
        {
            WorkCancellation.Check();
            Properties(entry, "an entry", ["member"], ["model", "refusal"]);
            if (entry.GetProperty("member") is not { ValueKind: JsonValueKind.String } member)
                throw new FormatException("an entry's member must be a string.");
            entries.Add(new RawEntry(member.GetString()!, entry.Clone()));
        }

        return entries;
    }

    /// <summary>Checks that a header names the file's key in every part: the assembly's name and version, and a package's id, version
    /// and MVID, or a framework's name and major.</summary>
    /// <param name="key">The key.</param>
    /// <param name="header">The <c>assembly</c> object of the file.</param>
    /// <exception cref="FormatException">The header does not name the key in some part.</exception>
    private static void Header(GeneratedFileKey key, JsonElement header)
    {
        Properties(header, "the assembly header", key.IsPackage ? ["name", "version", "mvid", "package", "packageVersion"]
                                                                : ["name", "version", "mvid", "framework", "major"], []);
        Part(header, "name", key.AssemblyName, "assembly name");
        Part(header, "version", key.AssemblyVersion.ToString(4), "assembly version");
        if (key.IsPackage)
        {
            Part(header, "package", key.Package!, "package id");
            Part(header, "packageVersion", key.PackageVersion!, "package version");
            if (header.GetProperty("mvid") is not { ValueKind: JsonValueKind.String } mvid || !Guid.TryParse(mvid.GetString(), out var parsed) ||
                parsed != key.Mvid)
                throw new FormatException("the assembly header does not name the file's mvid.");
            return;
        }

        if (header.GetProperty("mvid").ValueKind != JsonValueKind.Null)
            throw new FormatException("the assembly header of a framework file does not name a mvid.");
        Part(header, "framework", key.Framework!, "framework name");
        if (header.GetProperty("major") is not { ValueKind: JsonValueKind.Number } major || !major.TryGetInt32(out var value) || value != key.Major)
            throw new FormatException("the assembly header does not name the file's framework major.");
    }

    private static void Part(JsonElement header, string property, string expected, string part)
    {
        if (header.GetProperty(property) is not { ValueKind: JsonValueKind.String } value ||
            !string.Equals(value.GetString(), expected, StringComparison.Ordinal))
            throw new FormatException($"the assembly header does not name the file's {part}.");
    }

    /// <summary>What an entry holds: the reason it is invalid, or <c>null</c> when it is valid.</summary>
    /// <param name="displayPath">The file's path as a diagnostic writes it.</param>
    /// <param name="raw">The entry, its member and its JSON.</param>
    /// <param name="entry">The entry when it is valid; otherwise <c>null</c>.</param>
    private static string? Check(string displayPath, RawEntry raw, out GeneratedModelEntry? entry)
    {
        entry = null;
        if (raw.Member.Length == 0)
            return "the member is empty.";
        var hasModel = raw.Element.TryGetProperty("model", out var model);
        var hasRefusal = raw.Element.TryGetProperty("refusal", out var refusalValue);
        if (hasModel == hasRefusal)
            return "an entry holds exactly one of a model and a refusal.";
        if (hasRefusal)
        {
            GeneratedRefusal refusal;
            try
            {
                refusal = Refusal(refusalValue);
            }
            catch (FormatException error)
            {
                return error.Message;
            }

            if (!Keeps(refusal.Outcome))
                return $"a file does not keep the reason '{refusal.Reason}'.";
            entry = new GeneratedModelEntry(displayPath, raw.Member, null, refusal);
            return null;
        }

        var project = ProjectModelFiles.Read(displayPath, Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"models\":[" + model.GetRawText() + "]}"));
        if (project.Rejections.Count != 0)
            return project.Rejections[0].Reason;
        if (project.Entries is not [{ } projectEntry])
            return "the model is not one entry.";
        if (projectEntry.Opaque)
            return "the generated layer holds models and refusals only, not an opaque entry.";
        if (!string.Equals(projectEntry.Member, raw.Member, StringComparison.Ordinal))
            return $"the entry answers {raw.Member} but its model describes {projectEntry.Member}.";
        if (!Keeps(GenerationOutcome.Model))
            return "a file does not keep a model.";
        entry = new GeneratedModelEntry(displayPath, raw.Member, projectEntry, null);
        return null;
    }

    private static GeneratedRefusal Refusal(JsonElement refusal)
    {
        Properties(refusal, "the refusal", ["reason", "detail", "causes"], []);
        if (refusal.GetProperty("reason") is not { ValueKind: JsonValueKind.String } reason || refusal.GetProperty("detail") is not { ValueKind: JsonValueKind.String } detail ||
            refusal.GetProperty("causes") is not { ValueKind: JsonValueKind.Array } causes ||
            causes.EnumerateArray().Any(cause => cause.ValueKind != JsonValueKind.String))
            throw new FormatException("a refusal needs a reason and a detail that are strings and causes that are an array of strings.");
        return new GeneratedRefusal(reason.GetString()!, detail.GetString()!, causes.EnumerateArray().Select(cause => cause.GetString()!).ToArray());
    }

    /// <summary>Checks an object has every required property and no property outside the required and the optional, and none twice.</summary>
    /// <param name="element">The object.</param>
    /// <param name="what">What the object is, for the message.</param>
    /// <param name="required">The properties it must have.</param>
    /// <param name="optional">The properties it may have.</param>
    /// <exception cref="FormatException">The object breaks the rule.</exception>
    private static void Properties(JsonElement element, string what, string[] required, string[] optional)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{what} must be a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            WorkCancellation.Check();
            if (!seen.Add(property.Name) || !required.Contains(property.Name, StringComparer.Ordinal) &&
                !optional.Contains(property.Name, StringComparer.Ordinal))
                throw new FormatException($"{what} has a repeated or unknown property '{property.Name}'.");
        }

        if (required.FirstOrDefault(name => !seen.Contains(name)) is { } missing)
            throw new FormatException($"{what} is missing the property '{missing}'.");
    }

    private static GeneratedModelFile Empty(GeneratedFileKey key, string displayPath) =>
        new(key, displayPath, null, new Dictionary<string, GeneratedModelEntry>(StringComparer.Ordinal), []);

    private static GeneratedModelFile Unreadable(GeneratedFileKey key, string displayPath, string reason) =>
        new(key, displayPath, reason, new Dictionary<string, GeneratedModelEntry>(StringComparer.Ordinal), [Diagnostic(displayPath, reason)]);

    private static string Diagnostic(string displayPath, string text) => $"library-models: generated file {displayPath}: {text}";

    private sealed record RawEntry(string Member, JsonElement Element);
}
