using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace ConcurrencyHunter.Providers.LibraryModels.Sources;

public enum LibraryCodeKind { OriginalSources, DecompiledBest, DecompiledForRun }
public enum LibraryCodeReason { NoPdb, NoSources, PersistentFailure, TemporaryFailure, Offline, Cancelled, SourcesRejected, SourcesNotSought }
public enum SourceOutcome { Received, Persistent, Transient, UnavailableOffline, NoSourceAddress, Cancelled }
public enum HostFailure { None, Timeout, Transport, Cancelled, InsecureAddress, Redirect, Checksum, ForeignPdb, InvalidContent }

public sealed record LibrarySourceDocument(string Path, ImmutableArray<byte> Content)
{
    public SourceText Text
    {
        get
        {
            using var stream = new MemoryStream(Content.ToArray(), writable: false);
            return SourceText.From(stream, encoding: null);
        }
    }
}

/// <summary>The sources and preparation outcomes for one caller.</summary>
/// <param name="Kind">The code available before compilation.</param>
/// <param name="Reason">Why decompiled code is needed, or null for sources.</param>
/// <param name="Documents">The documents whose checksums matched.</param>
/// <param name="Defines">The PDB's preprocessor symbols.</param>
/// <param name="LanguageVersion">The PDB's C# language version.</param>
/// <param name="PdbId">The portable PDB identity, or null when unavailable.</param>
public sealed record LibrarySourceResult(LibraryCodeKind Kind, LibraryCodeReason? Reason,
                                        IReadOnlyList<LibrarySourceDocument> Documents, IReadOnlyList<string> Defines,
                                        LanguageVersion LanguageVersion, string? PdbId)
{
    /// <summary>The request failure that decided the code reason, or null when no request decided it.</summary>
    public string? Detail { get; init; } = Reason == LibraryCodeReason.Offline ? "offline" : null;

    /// <summary>Failures for documents with no methods; they matter only when sources fail to compile.</summary>
    public IReadOnlyList<SourceOutcome> MissingMethodlessDocuments { get; init; } = [];

    internal IReadOnlyList<HostClassification> MissingMethodlessFailures { get; init; } = [];
}

/// <summary>The code used by one generation, independent of a shared compilation.</summary>
/// <param name="Kind">Original sources or the finality of the decompiled code.</param>
/// <param name="Reason">Why decompiled code was used, or null for original sources.</param>
public sealed record LibraryCode(LibraryCodeKind Kind, LibraryCodeReason? Reason)
{
    /// <summary>The request failure that decided the reason, or null when no request decided it.</summary>
    public string? Detail { get; init; } = Reason == LibraryCodeReason.Offline ? "offline" : null;
}

internal readonly record struct HostClassification(SourceOutcome Outcome, bool SkipHost, string? Detail = null)
{
    internal bool Retry { get; init; }
    internal TimeSpan? RetryAfter { get; init; }
}

internal static class LibrarySourceDecisions
{
    internal static LibraryCode ForCompilation(LibrarySourceResult? prepared, bool sourcesRejected = false)
    {
        if (prepared is null)
            return new(LibraryCodeKind.DecompiledBest, LibraryCodeReason.SourcesNotSought);
        if (!sourcesRejected)
            return new(prepared.Kind, prepared.Reason) { Detail = prepared.Detail ?? (prepared.Reason == LibraryCodeReason.Offline ? "offline" : null) };
        var decision = Decide(true, true, [], [], compilationRefused: true, prepared.MissingMethodlessDocuments);
        return new(decision.Kind, decision.Reason) { Detail = Detail(decision.Reason, prepared.MissingMethodlessFailures) };
    }

    internal static string? Detail(LibraryCodeReason? reason, IReadOnlyList<HostClassification> failures)
    {
        if (reason == LibraryCodeReason.Offline)
            return "offline";
        var outcome = reason switch
        {
            LibraryCodeReason.NoPdb or LibraryCodeReason.PersistentFailure => SourceOutcome.Persistent,
            LibraryCodeReason.NoSources => SourceOutcome.NoSourceAddress,
            LibraryCodeReason.TemporaryFailure => SourceOutcome.Transient,
            LibraryCodeReason.Cancelled => SourceOutcome.Cancelled,
            _ => (SourceOutcome?)null
        };
        return failures.FirstOrDefault(item => item.Outcome == outcome).Detail;
    }

    internal static HostClassification ClassifyTransport(Uri address, int? status, string error) =>
        Classify(address, status, status is null ? HostFailure.Transport : HostFailure.None, error: error);

    // bounded: classifies one response through the status/failure overload, which does not recurse.
    internal static HostClassification Classify(Uri address, int? status = null, HostFailure failure = HostFailure.None,
                                               string? error = null, TimeSpan? timeout = null)
    {
        var classification = Classify(status, failure);
        if (classification.Outcome == SourceOutcome.Received)
            return classification;
        var detail = failure switch
        {
            HostFailure.Timeout => "no answer in " + (timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture) + " s",
            HostFailure.Transport => "connection error: " + error,
            HostFailure.Cancelled => "cancelled",
            HostFailure.None => "HTTP " + status + (error is null ? "" : "; " + error),
            _ => (status is null ? "" : "HTTP " + status + "; ") + failure
        };
        return classification with { Detail = address.AbsoluteUri + " (host " + address.IdnHost + "): " + detail };
    }

    internal static HostClassification Classify(int? status = null, HostFailure failure = HostFailure.None)
    {
        if (failure == HostFailure.Timeout)
            return new(SourceOutcome.Transient, true);
        if (failure == HostFailure.Cancelled)
            return new(SourceOutcome.Cancelled, false);
        if (failure == HostFailure.Transport)
            return new(SourceOutcome.Transient, false);
        if (failure != HostFailure.None)
            return new(SourceOutcome.Persistent, false);
        return status switch
        {
            200 => new(SourceOutcome.Received, false),
            401 or 403 or 407 => new(SourceOutcome.Persistent, true),
            404 or 410 => new(SourceOutcome.Persistent, false),
            429 or 503 => new(SourceOutcome.Transient, false) { Retry = true },
            _ => new(SourceOutcome.Transient, false)
        };
    }

    internal static (LibraryCodeKind Kind, LibraryCodeReason? Reason) Decide(bool portableCodeView, bool pdbFound,
        IReadOnlyList<SourceOutcome> pdbOutcomes, IReadOnlyList<SourceOutcome> documents, bool compilationRefused = false,
        IReadOnlyList<SourceOutcome>? missingMethodlessDocuments = null)
    {
        if (!portableCodeView)
            return (LibraryCodeKind.DecompiledBest, LibraryCodeReason.NoPdb);
        if (!pdbFound)
        {
            if (pdbOutcomes.Contains(SourceOutcome.Cancelled))
                return (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.Cancelled);
            if (pdbOutcomes.Contains(SourceOutcome.UnavailableOffline))
                return (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.Offline);
            if (pdbOutcomes.Contains(SourceOutcome.Transient))
                return (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.TemporaryFailure);
            return (LibraryCodeKind.DecompiledBest, LibraryCodeReason.NoPdb);
        }
        if (documents.Contains(SourceOutcome.NoSourceAddress))
            return (LibraryCodeKind.DecompiledBest, LibraryCodeReason.NoSources);
        if (documents.Contains(SourceOutcome.Persistent))
            return (LibraryCodeKind.DecompiledBest, LibraryCodeReason.PersistentFailure);
        if (documents.Contains(SourceOutcome.Cancelled))
            return (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.Cancelled);
        if (documents.Contains(SourceOutcome.UnavailableOffline))
            return (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.Offline);
        if (documents.Contains(SourceOutcome.Transient))
            return (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.TemporaryFailure);
        if (!compilationRefused)
            return (LibraryCodeKind.OriginalSources, null);
        if (missingMethodlessDocuments?.Contains(SourceOutcome.Cancelled) is true)
            return (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.Cancelled);
        if (missingMethodlessDocuments?.Contains(SourceOutcome.UnavailableOffline) is true)
            return (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.Offline);
        if (missingMethodlessDocuments?.Contains(SourceOutcome.Transient) is true)
            return (LibraryCodeKind.DecompiledForRun, LibraryCodeReason.TemporaryFailure);
        return (LibraryCodeKind.DecompiledBest, LibraryCodeReason.SourcesRejected);
    }
}
