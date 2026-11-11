using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Fixtures;

internal sealed class SourceLibrary : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ch-library-sources-" + Guid.NewGuid().ToString("N"));
    internal string Dll => Path.Combine(Root, "library.dll");
    internal string Pdb => Path.Combine(Root, "library.pdb");
    internal string Cache => Path.Combine(Root, "cache");
    internal byte[] PdbBytes { get; }
    internal string SymbolKey { get; }
    internal IReadOnlyDictionary<string, byte[]> Documents { get; }

    internal SourceLibrary(Uri sourceServer, bool adjacent = false, bool embeddedPdb = false, bool embeddedDocuments = false,
                           bool crlf = false, bool checksum = true, bool sourceLink = true, bool optionalDocument = false,
                           int documentCount = 2, bool partialSourceLink = false)
    {
        Directory.CreateDirectory(Root);
        var documents = new Dictionary<string, byte[]>();
        for (var index = 0; index < documentCount; index++)
        {
            var code = $"public class C{index}\n{{\n    public int M() {{ return {index}; }}\n}}\n";
            if (crlf) code = code.Replace("\n", "\r\n", StringComparison.Ordinal);
            documents[$"/src/file{index}.cs"] = Encoding.UTF8.GetBytes(code);
        }
        if (optionalDocument)
            documents["/src/AssemblyInfo.cs"] = Encoding.UTF8.GetBytes("[assembly: System.Reflection.AssemblyVersion(\"3.0.0.0\")]\n");
        Documents = documents;
        var parse = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: ["SOURCE_TEST", "SECOND"]);
        var texts = documents.ToDictionary(pair => pair.Key, pair => SourceText.From(new MemoryStream(pair.Value), Encoding.UTF8,
            SourceHashAlgorithm.Sha256, canBeEmbedded: true));
        var compilation = CSharpCompilation.Create("library", texts.Select(pair => CSharpSyntaxTree.ParseText(pair.Value, parse, pair.Key)),
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        using var links = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
        {
            documents = partialSourceLink
                ? new Dictionary<string, string> { ["/src/file0.cs"] = new Uri(sourceServer, "sources/file0.cs").AbsoluteUri }
                : new Dictionary<string, string> { ["/src/*"] = new Uri(sourceServer, "sources/*").AbsoluteUri }
        }));
        var emitted = compilation.Emit(pe, pdbStream: embeddedPdb ? null : pdb,
            options: new EmitOptions(debugInformationFormat: embeddedPdb ? DebugInformationFormat.Embedded : DebugInformationFormat.PortablePdb,
                                     pdbFilePath: "library.pdb", pdbChecksumAlgorithm: HashAlgorithmName.SHA256),
            sourceLinkStream: sourceLink ? links : null,
            embeddedTexts: embeddedDocuments ? texts.Select(pair => EmbeddedText.FromSource(pair.Key, pair.Value)) : null);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var image = pe.ToArray();
        using (var reader = new PEReader(new MemoryStream(image)))
        {
            var entries = reader.ReadDebugDirectory();
            var entry = entries.Single(item => item.Type == DebugDirectoryEntryType.CodeView);
            var cv = reader.ReadCodeViewDebugDirectoryData(entry);
            SymbolKey = $"library.pdb/{cv.Guid:N}FFFFFFFF/library.pdb";
            if (embeddedPdb)
            {
                var embedded = entries.Single(item => item.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
                var block = image.AsSpan(embedded.DataPointer + 8, embedded.DataSize - 8).ToArray();
                using var deflate = new System.IO.Compression.DeflateStream(new MemoryStream(block), System.IO.Compression.CompressionMode.Decompress);
                deflate.CopyTo(pdb);
            }
        }
        PdbBytes = pdb.ToArray();
        File.WriteAllBytes(Dll, image);
        if (!checksum)
            RemoveChecksum();
        if (adjacent)
            File.WriteAllBytes(Pdb, PdbBytes);
    }

    internal void Serve(LoopbackSymbolServer server, string prefix = "symbols")
    {
        server.Replies[$"/{prefix}/{SymbolKey}"] = new(Bytes: PdbBytes);
        foreach (var (path, bytes) in Documents)
            server.Replies["/sources/" + path.Split('/')[^1]] = new(Bytes: bytes);
    }

    internal void RemoveChecksum() => EditDebug((image, offset, entries) =>
    {
        var index = Array.FindIndex(entries, entry => entry.Type == DebugDirectoryEntryType.PdbChecksum);
        image.AsSpan(offset + index * 28, 28).Clear();
    });

    internal void AddWindowsCodeViewFirst() => EditDebug((image, offset, entries) =>
    {
        var index = Array.FindIndex(entries, entry => entry.Type == DebugDirectoryEntryType.Reproducible);
        Assert.True(index > 0);
        image.AsSpan(offset, 28).CopyTo(image.AsSpan(offset + index * 28, 28));
        // Windows CodeView first, portable CodeView last. The different stamp makes taking the first observably wrong.
        image.AsSpan(offset + 10, 2).Clear();
        image.AsSpan(offset + 4, 4).Clear();
    });

    internal void RemovePortableCodeView() => EditDebug((image, offset, entries) =>
    {
        for (var index = 0; index < entries.Length; index++)
            if (entries[index].Type == DebugDirectoryEntryType.CodeView)
                image.AsSpan(offset + index * 28 + 10, 2).Clear();
    });

    private void EditDebug(Action<byte[], int, DebugDirectoryEntry[]> edit)
    {
        var image = File.ReadAllBytes(Dll);
        using var reader = new PEReader(new MemoryStream(image));
        var rva = reader.PEHeaders.PEHeader!.DebugTableDirectory.RelativeVirtualAddress;
        var section = reader.PEHeaders.SectionHeaders.Single(item => rva >= item.VirtualAddress && rva < item.VirtualAddress + item.SizeOfRawData);
        edit(image, rva - section.VirtualAddress + section.PointerToRawData, reader.ReadDebugDirectory().ToArray());
        File.WriteAllBytes(Dll, image);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
