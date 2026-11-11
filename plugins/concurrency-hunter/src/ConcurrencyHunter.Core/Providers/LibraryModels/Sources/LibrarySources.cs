using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConcurrencyHunter.Analysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ConcurrencyHunter.Providers.LibraryModels.Sources;

public sealed class LibrarySources : IDisposable
{
    private static readonly Guid EmbeddedSource = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    private static readonly Guid SourceLink = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");
    private static readonly Guid CompilationOptions = new("B5FEEC05-8CD0-4A83-96DA-466284BB4BD8");
    private static readonly Guid Sha1 = new("FF1816EC-AA5E-4D10-87F7-6F4963833460");
    private static readonly Guid Sha256 = new("8829D00F-11B8-4213-878B-770E8597AC16");
    private readonly bool _offline;
    private readonly bool _httpsOnly;
    private readonly string _cacheRoot;
    private readonly Uri[] _servers;
    private readonly TimeSpan _timeout;
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, Lazy<Task<LibrarySourceResult>>> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HostClassification> _skippedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LibraryCodeKind> _code = new(StringComparer.OrdinalIgnoreCase);
    private long _requests;
    private long _bytes;
    private long _downloadTicks;
    internal Func<string, CancellationToken, Task>? CacheWriting { get; init; }
    internal TimeProvider RequestTime { get; init; } = TimeProvider.System;

    public LibrarySources(bool offline, string cacheRoot, Uri? microsoftServer = null, Uri? nugetServer = null,
                          bool httpsOnly = true, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        _offline = offline;
        _httpsOnly = httpsOnly;
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _servers = [microsoftServer ?? new Uri("https://msdl.microsoft.com/download/symbols/"),
                    nugetServer ?? new Uri("https://symbols.nuget.org/download/symbols/")];
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        handler ??= new SocketsHttpHandler();
        // Redirects are checked here, before another host sees a request. Authentication and cookies never leave this component.
        if (handler is SocketsHttpHandler sockets)
        {
            sockets.AllowAutoRedirect = false;
            sockets.UseCookies = false;
            sockets.Credentials = null;
            sockets.PreAuthenticate = false;
            sockets.UseProxy = httpsOnly;
            sockets.Proxy = httpsOnly ? new AnonymousProxy(HttpClient.DefaultProxy) : null;
            sockets.DefaultProxyCredentials = null;
        }
        else if (handler is HttpClientHandler http)
        {
            http.AllowAutoRedirect = false;
            http.UseCookies = false;
            http.UseDefaultCredentials = false;
            http.Credentials = null;
            http.PreAuthenticate = false;
            http.UseProxy = httpsOnly;
            http.Proxy = httpsOnly ? new AnonymousProxy(HttpClient.DefaultProxy) : null;
            http.DefaultProxyCredentials = null;
        }
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class AnonymousProxy(IWebProxy proxy) : IWebProxy
    {
        public ICredentials? Credentials { get => null; set { } }

        // bounded: forwards once to the configured system proxy.
        public Uri? GetProxy(Uri destination)
        {
            var address = proxy.GetProxy(destination);
            return address is null || string.IsNullOrEmpty(address.UserInfo) ? address
                : new UriBuilder(address) { UserName = "", Password = "" }.Uri;
        }

        // bounded: forwards once to the configured system proxy.
        public bool IsBypassed(Uri host) => proxy.IsBypassed(host);
    }

    public Task<LibrarySourceResult> GetAsync(string assemblyPath, CancellationToken cancellationToken = default) =>
        _results.GetOrAdd(Path.GetFullPath(assemblyPath), path => new Lazy<Task<LibrarySourceResult>>(
            async () =>
            {
                var result = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
                _code.TryAdd(path, result.Kind);
                return result;
            }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    public SourceStatistics Statistics => new(_offline,
        new(_code.Values.Count(kind => kind == LibraryCodeKind.OriginalSources),
            _code.Values.Count(kind => kind == LibraryCodeKind.DecompiledBest),
            _code.Values.Count(kind => kind == LibraryCodeKind.DecompiledForRun)),
        new(Interlocked.Read(ref _requests), Interlocked.Read(ref _bytes),
            (double)Interlocked.Read(ref _downloadTicks) / Stopwatch.Frequency));

    internal void RecordCode(string assemblyPath, LibraryCode code) => _code[Path.GetFullPath(assemblyPath)] = code.Kind;

    private async Task<LibrarySourceResult> ReadAsync(string path, CancellationToken cancellationToken)
    {
        using var work = WorkCancellation.Enter(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var entries = pe.ReadDebugDirectory();
            var portable = entries.FirstOrDefault(entry => entry.Type == DebugDirectoryEntryType.CodeView && entry.MinorVersion == 0x504d);
            if (portable.Type != DebugDirectoryEntryType.CodeView)
                return Result(false, false, [], []);
            var codeView = pe.ReadCodeViewDebugDirectoryData(portable);
            var id = new BlobContentId(codeView.Guid, portable.Stamp);
            var pdbId = codeView.Guid.ToString("N") + portable.Stamp.ToString("X8");
            var checksumEntry = entries.FirstOrDefault(entry => entry.Type == DebugDirectoryEntryType.PdbChecksum);
            var checksum = checksumEntry.Type == DebugDirectoryEntryType.PdbChecksum
                ? pe.ReadPdbChecksumDebugDirectoryData(checksumEntry) : (PdbChecksumDebugDirectoryData?)null;
            var name = codeView.Path.Replace('\\', '/').Split('/')[^1].ToLowerInvariant();
            var cachePdb = Path.Combine(_cacheRoot, pdbId, "symbols.pdb");
            byte[]? pdb = null;
            var embedded = entries.FirstOrDefault(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
            if (embedded.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
            {
                try
                {
                    var data = pe.GetSectionData(embedded.DataRelativeVirtualAddress).GetContent(0, embedded.DataSize).ToArray();
                    if (data.Length >= 8 && BitConverter.ToInt32(data, 0) == 0x4244504d)
                        pdb = Inflate(data.AsSpan(8).ToArray());
                }
                catch (InvalidDataException) { }
                if (pdb is not null && ValidatePdb(pdb, id, checksum) != HostFailure.None)
                    pdb = null;
            }
            var adjacent = Path.Combine(Path.GetDirectoryName(path)!, codeView.Path.Replace('\\', '/').Split('/')[^1]);
            pdb ??= await ReadPdbAsync(adjacent, id, checksum, cancellationToken).ConfigureAwait(false);
            pdb ??= await ReadPdbAsync(cachePdb, id, checksum, cancellationToken).ConfigureAwait(false);
            var outcomes = new List<HostClassification>();
            if (pdb is null)
            {
                if (_offline)
                    outcomes.Add(new(SourceOutcome.UnavailableOffline, false, "offline"));
                else
                {
                    var key = $"{name}/{codeView.Guid:N}FFFFFFFF/{name}";
                    // bounded: the two symbol servers in lookup order.
                    for (var index = 0; index < _servers.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (index == 1 && checksum is null)
                            continue;
                        var header = index == 1 && checksum is { } sum
                            ? sum.AlgorithmName + ":" + Convert.ToHexString(sum.Checksum.AsSpan()) : null;
                        var reply = await FetchAsync(new Uri(_servers[index].AbsoluteUri.TrimEnd('/') + "/" + key),
                            bytes => ValidatePdb(bytes, id, checksum), header, cancellationToken).ConfigureAwait(false);
                        outcomes.Add(reply.Classification);
                        if (reply.Bytes is not null)
                        {
                            pdb = reply.Bytes;
                            break;
                        }
                    }
                }
            }
            if (pdb is null)
                return Result(true, false, outcomes, [], pdbId: pdbId);
            await WriteCacheAsync(cachePdb, pdb, cancellationToken).ConfigureAwait(false);
            try { return await ReadDocumentsAsync(pdb, pdbId, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is BadImageFormatException or InvalidDataException or JsonException)
            {
                return Result(true, true, [], [new(SourceOutcome.Persistent, false)], pdbId: pdbId);
            }
        }
        catch (OperationCanceledException)
        {
            return Result(true, false, [new(SourceOutcome.Cancelled, false)], []);
        }
        catch (Exception exception) when (exception is BadImageFormatException or InvalidDataException or JsonException)
        {
            return Result(true, false, [new(SourceOutcome.Persistent, false)], []);
        }
    }

    private async Task<LibrarySourceResult> ReadDocumentsAsync(byte[] pdb, string pdbId, CancellationToken cancellationToken)
    {
        using var provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create(pdb));
        var reader = provider.GetMetadataReader();
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        var defines = Array.Empty<string>();
        var language = LanguageVersion.Default;
        var required = new HashSet<DocumentHandle>();
        foreach (var handle in reader.MethodDebugInformation)
        {
            WorkCancellation.Check();
            cancellationToken.ThrowIfCancellationRequested();
            var method = reader.GetMethodDebugInformation(handle);
            if (!method.Document.IsNil)
                required.Add(method.Document);
            foreach (var point in method.GetSequencePoints())
            {
                WorkCancellation.Check();
                cancellationToken.ThrowIfCancellationRequested();
                if (!point.Document.IsNil)
                    required.Add(point.Document);
            }
        }
        foreach (var handle in reader.CustomDebugInformation)
        {
            WorkCancellation.Check();
            cancellationToken.ThrowIfCancellationRequested();
            var item = reader.GetCustomDebugInformation(handle);
            if (item.Parent.Kind != HandleKind.ModuleDefinition)
                continue;
            var kind = reader.GetGuid(item.Kind);
            var bytes = reader.GetBlobBytes(item.Value);
            if (kind == SourceLink)
            {
                using var json = JsonDocument.Parse(bytes);
                foreach (var link in json.RootElement.GetProperty("documents").EnumerateObject())
                {
                    WorkCancellation.Check();
                    cancellationToken.ThrowIfCancellationRequested();
                    links[link.Name] = link.Value.GetString()!;
                }
            }
            else if (kind == CompilationOptions)
            {
                var parts = Encoding.UTF8.GetString(bytes).Split('\0');
                for (var index = 0; index + 1 < parts.Length; index += 2)
                {
                    WorkCancellation.Check();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (parts[index] == "define")
                        defines = parts[index + 1].Split([',', ';'], StringSplitOptions.RemoveEmptyEntries);
                    else if (parts[index] == "language-version")
                        LanguageVersionFacts.TryParse(parts[index + 1], out language);
                }
            }
        }
        var documents = new List<LibrarySourceDocument>();
        var outcomes = new List<HostClassification>();
        var missingMethodless = new List<HostClassification>();
        try
        {
            foreach (var handle in reader.Documents)
            {
                WorkCancellation.Check();
                cancellationToken.ThrowIfCancellationRequested();
                var document = reader.GetDocument(handle);
                var name = reader.GetString(document.Name);
                var algorithm = reader.GetGuid(document.HashAlgorithm);
                var expected = reader.GetBlobBytes(document.Hash);
                var cache = Path.Combine(_cacheRoot, pdbId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))) + ".source");
                byte[]? bytes = null;
                foreach (var debugHandle in reader.GetCustomDebugInformation(handle))
                {
                    WorkCancellation.Check();
                    cancellationToken.ThrowIfCancellationRequested();
                    var debug = reader.GetCustomDebugInformation(debugHandle);
                    if (reader.GetGuid(debug.Kind) != EmbeddedSource)
                        continue;
                    var blob = reader.GetBlobBytes(debug.Value);
                    if (blob.Length < 4)
                        continue;
                    try { bytes = BitConverter.ToInt32(blob, 0) == 0 ? blob[4..] : Inflate(blob[4..]); }
                    catch (InvalidDataException) { }
                    bytes = MatchDocument(bytes, algorithm, expected);
                }
                bytes ??= MatchDocument(await ReadFileAsync(cache, cancellationToken).ConfigureAwait(false), algorithm, expected);
                var outcome = new HostClassification(SourceOutcome.Received, false);
                if (bytes is null)
                {
                    var url = DocumentUrl(name, links, cancellationToken);
                    if (url is null)
                        outcome = new(SourceOutcome.NoSourceAddress, false, name + ": no Source Link address");
                    else if (!Allowed(url))
                        outcome = LibrarySourceDecisions.Classify(url, failure: HostFailure.InsecureAddress);
                    else if (_offline)
                        outcome = new(SourceOutcome.UnavailableOffline, false, "offline");
                    else
                    {
                        var reply = await FetchAsync(url, content => MatchDocument(content, algorithm, expected) is null
                            ? HostFailure.Checksum : HostFailure.None, null, cancellationToken).ConfigureAwait(false);
                        bytes = MatchDocument(reply.Bytes, algorithm, expected);
                        outcome = reply.Classification;
                    }
                }
                if (bytes is not null)
                {
                    documents.Add(new(name, ImmutableArray.Create(bytes)));
                    await WriteCacheAsync(cache, bytes, cancellationToken).ConfigureAwait(false);
                }
                else if (required.Contains(handle))
                    outcomes.Add(outcome);
                else
                    missingMethodless.Add(outcome);
            }
        }
        catch (OperationCanceledException) { outcomes.Add(new(SourceOutcome.Cancelled, false)); }
        return Result(true, true, [], outcomes, documents, defines, language, pdbId) with
        {
            MissingMethodlessDocuments = missingMethodless.Select(item => item.Outcome).ToArray(),
            MissingMethodlessFailures = missingMethodless
        };
    }

    private async Task<(byte[]? Bytes, HostClassification Classification)> FetchAsync(Uri uri, Func<byte[], HostFailure> validate,
                                                                                  string? checksum, CancellationToken cancellationToken)
    {
        // bounded: at most four attempts; only 429/503 are retried, and cancellation stops every wait.
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reply = await FetchAttemptAsync(uri, validate, checksum, cancellationToken).ConfigureAwait(false);
            if (!reply.Classification.Retry || attempt == 3)
                return reply;
            var delay = reply.Classification.RetryAfter ?? TimeSpan.FromSeconds(1 << attempt);
            await Task.Delay(delay, RequestTime, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(byte[]? Bytes, HostClassification Classification)> FetchAttemptAsync(Uri uri, Func<byte[], HostFailure> validate,
                                                                                         string? checksum, CancellationToken cancellationToken)
    {
        using var elapsed = new CancellationTokenSource(_timeout, RequestTime);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, elapsed.Token);
        var current = uri;
        // bounded: at most ten redirects, with the same timeout including the response body.
        for (var redirects = 0; redirects <= 10; redirects++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Allowed(current))
                return (null, LibrarySourceDecisions.Classify(current, failure: HostFailure.InsecureAddress));
            if (_skippedHosts.TryGetValue(current.IdnHost, out var skipped))
                return (null, skipped);
            HostClassification classification;
            var started = Stopwatch.GetTimestamp();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                if (checksum is not null)
                    request.Headers.TryAddWithoutValidation("SymbolChecksum", checksum);
                Interlocked.Increment(ref _requests);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is not { } location || redirects == 10 || !Uri.TryCreate(current, location, out var next))
                        return (null, LibrarySourceDecisions.Classify(current, status, HostFailure.Redirect));
                    current = next;
                    continue;
                }
                classification = LibrarySourceDecisions.Classify(current, status);
                if (classification.Retry)
                {
                    var retryAfter = response.Headers.RetryAfter;
                    var delay = retryAfter?.Delta ?? (retryAfter?.Date - RequestTime.GetUtcNow());
                    if (delay is { } wait)
                        classification = classification with { RetryAfter = TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 0, 60)) };
                }
                if (classification.Outcome == SourceOutcome.Received)
                {
                    using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using var received = new MemoryStream();
                    var buffer = new byte[81920];
                    int count;
                    // bounded: every read observes the same request timeout, including the response body.
                    while ((count = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Interlocked.Add(ref _bytes, count);
                        received.Write(buffer, 0, count);
                    }
                    var bytes = received.ToArray();
                    classification = LibrarySourceDecisions.Classify(current, status, validate(bytes));
                    if (classification.Outcome == SourceOutcome.Received)
                        return (bytes, classification);
                }
            }
            catch (OperationCanceledException)
            {
                classification = LibrarySourceDecisions.Classify(current, failure: cancellationToken.IsCancellationRequested
                    ? HostFailure.Cancelled : HostFailure.Timeout, timeout: _timeout);
            }
            catch (HttpRequestException exception)
            {
                classification = LibrarySourceDecisions.ClassifyTransport(current, (int?)exception.StatusCode,
                    exception.HttpRequestError + ": " + exception.Message);
            }
            catch (IOException exception) { classification = LibrarySourceDecisions.Classify(current, failure: HostFailure.Transport, error: exception.Message); }
            finally { Interlocked.Add(ref _downloadTicks, Stopwatch.GetTimestamp() - started); }
            if (classification.SkipHost)
                _skippedHosts.TryAdd(current.IdnHost, classification);
            return (null, classification);
        }
        return (null, LibrarySourceDecisions.Classify(current, failure: HostFailure.Redirect));
    }

    private bool Allowed(Uri uri) => string.IsNullOrEmpty(uri.UserInfo) && (uri.Scheme == Uri.UriSchemeHttps
        || !_httpsOnly && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    private static HostFailure ValidatePdb(byte[] bytes, BlobContentId id, PdbChecksumDebugDirectoryData? checksum)
    {
        try
        {
            using var provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create(bytes));
            var reader = provider.GetMetadataReader();
            if (reader.DebugMetadataHeader is not { } header || new BlobContentId(header.Id) != id)
                return HostFailure.ForeignPdb;
            if (checksum is { } sum)
            {
                // Portable PDB checksums are computed before the content id is written into the #Pdb stream.
                var content = (byte[])bytes.Clone();
                content.AsSpan(header.IdStartOffset, header.Id.Length).Clear();
                if (!MatchesHash(content, sum.AlgorithmName, sum.Checksum.AsSpan()))
                    return HostFailure.Checksum;
            }
            return HostFailure.None;
        }
        catch (BadImageFormatException) { return HostFailure.InvalidContent; }
    }

    private static async Task<byte[]?> ReadPdbAsync(string path, BlobContentId id, PdbChecksumDebugDirectoryData? checksum,
                                                  CancellationToken cancellationToken)
    {
        var bytes = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        return bytes is not null && ValidatePdb(bytes, id, checksum) == HostFailure.None ? bytes : null;
    }

    private static async Task<byte[]?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        try { return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private async Task WriteCacheAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? temporary = null;
        try
        {
            // A directory is created only after a complete, validated payload exists.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                                    4096, FileOptions.Asynchronous))
            {
                var middle = bytes.Length / 2;
                await output.WriteAsync(bytes.AsMemory(0, middle), cancellationToken).ConfigureAwait(false);
                if (CacheWriting is { } writing)
                    await writing(path, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(bytes.AsMemory(middle), cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static byte[]? MatchDocument(byte[]? bytes, Guid algorithm, byte[] expected)
    {
        if (bytes is null || expected.Length == 0)
            return null;
        var hash = algorithm == Sha256 ? "SHA256" : algorithm == Sha1 ? "SHA1" : "";
        if (MatchesHash(bytes, hash, expected))
            return bytes;
        // Change only bare LF code units, retaining every other byte, including malformed text and the BOM.
        var width = bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })
            || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }) ? 4
            : bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) || bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) ? 2 : 1;
        var bigEndian = width > 1 && bytes[0] != 0xff;
        using var output = new MemoryStream();
        var previous = -1;
        for (var index = 0; index < bytes.Length; index += width)
        {
            WorkCancellation.Check();
            var count = Math.Min(width, bytes.Length - index);
            var unit = bytes.AsSpan(index, count);
            var value = width == 1 ? bytes[index] : count != width ? -1 : width == 2
                ? (bigEndian ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(unit)
                             : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(unit))
                : (long)(bigEndian ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(unit)
                                   : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(unit));
            if (value == 10 && previous != 13)
            {
                var carriageReturn = new byte[width];
                carriageReturn[bigEndian ? width - 1 : 0] = 13;
                output.Write(carriageReturn);
            }
            output.Write(unit);
            previous = (int)value;
        }
        var converted = output.ToArray();
        return MatchesHash(converted, hash, expected) ? converted : null;
    }

    private static bool MatchesHash(byte[] bytes, string algorithm, ReadOnlySpan<byte> expected)
    {
        var actual = algorithm.ToUpperInvariant() switch
        {
            "SHA256" => SHA256.HashData(bytes),
            "SHA1" => SHA1.HashData(bytes),
            "SHA384" => SHA384.HashData(bytes),
            "SHA512" => SHA512.HashData(bytes),
            _ => []
        };
        return actual.Length > 0 && actual.AsSpan().SequenceEqual(expected);
    }

    private static Uri? DocumentUrl(string path, Dictionary<string, string> links, CancellationToken cancellationToken)
    {
        if (links.TryGetValue(path, out var exact))
            return Uri.TryCreate(exact, UriKind.Absolute, out var uri) ? uri : null;
        foreach (var (pattern, value) in links.OrderByDescending(link => link.Key.Length))
        {
            WorkCancellation.Check();
            cancellationToken.ThrowIfCancellationRequested();
            var star = pattern.IndexOf('*');
            if (star < 0 || !path.StartsWith(pattern[..star], StringComparison.Ordinal)
                || !path.EndsWith(pattern[(star + 1)..], StringComparison.Ordinal) || path.Length < pattern.Length - 1)
                continue;
            var replacement = path.Substring(star, path.Length - pattern.Length + 1);
            return Uri.TryCreate(value.Replace("*", replacement, StringComparison.Ordinal), UriKind.Absolute, out var uri) ? uri : null;
        }
        return null;
    }

    private static byte[] Inflate(byte[] bytes)
    {
        using var compressed = new MemoryStream(bytes, writable: false);
        using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static LibrarySourceResult Result(bool portable, bool found, IReadOnlyList<HostClassification> pdb,
        IReadOnlyList<HostClassification> outcomes, IReadOnlyList<LibrarySourceDocument>? documents = null,
        IReadOnlyList<string>? defines = null, LanguageVersion language = LanguageVersion.Default, string? pdbId = null)
    {
        var decision = LibrarySourceDecisions.Decide(portable, found, pdb.Select(item => item.Outcome).ToArray(),
            outcomes.Select(item => item.Outcome).ToArray());
        return new(decision.Kind, decision.Reason, documents ?? [], defines ?? [], language, pdbId)
        {
            Detail = LibrarySourceDecisions.Detail(decision.Reason, found ? outcomes : pdb)
        };
    }

    // bounded: disposes the single HTTP client owned by this instance.
    public void Dispose() => _client.Dispose();
}
