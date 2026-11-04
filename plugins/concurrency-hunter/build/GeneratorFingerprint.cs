using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

public static class GeneratorFingerprint
{
    private static readonly CSharpParseOptions SourceOptions = new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.None);

    public static IReadOnlyList<string> Inputs(string pluginsRoot)
    {
        var root = Path.GetFullPath(pluginsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projects = new[] { "ConcurrencyHunter.Analysis", "ConcurrencyHunter.Core" };
        var inputs = new List<string> { "Directory.Build.props", "Directory.Packages.props" };
        foreach (var project in projects)
        {
            var directory = Path.Combine(root, "concurrency-hunter", "src", project);
            inputs.Add("concurrency-hunter/src/" + project + "/" + project + ".csproj");
            inputs.AddRange(Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Select(path => path.Substring(root.Length + 1).Replace('\\', '/'))
                .Where(path => !path.Split('/').Any(part => part == "bin" || part == "obj")));
        }
        var builtIn = Path.Combine(root, "concurrency-hunter", "src", "ConcurrencyHunter.Core", "Providers", "LibraryModels", "BuiltIn");
        inputs.AddRange(Directory.EnumerateFiles(builtIn, "*.json")
            .Select(path => path.Substring(root.Length + 1).Replace('\\', '/')));
        return inputs.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The generator version (ADR 0023): a SHA-256 over each input's path and content, with line endings normalised. A C# source
    /// counts by its syntax tokens, so an edit of comments or whitespace alone keeps the version; every other input counts by its
    /// bytes. The sources are parsed in parallel: parsed one by one they made the build step about twice as slow.
    /// </summary>
    /// <param name="pluginsRoot">The repository's <c>plugins</c> folder, which holds <c>concurrency-hunter</c> and the shared build
    /// properties.</param>
    /// <returns>The fingerprint as 64 lowercase hex digits.</returns>
    public static string Compute(string pluginsRoot)
    {
        var inputs = Inputs(pluginsRoot);
        var contents = inputs.AsParallel().AsOrdered()
                             .Select(path => Content(Path.Combine(pluginsRoot, path), path.EndsWith(".cs", StringComparison.Ordinal)))
                             .ToArray();
        using (var stream = new MemoryStream())
        {
            for (var index = 0; index < inputs.Count; index++)
            {
                var name = Encoding.UTF8.GetBytes(inputs[index]);
                stream.Write(name, 0, name.Length);
                stream.WriteByte(0);
                stream.Write(contents[index], 0, contents[index].Length);
                stream.WriteByte(0);
            }
            stream.Position = 0;
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }

    /// <summary>
    /// What one input contributes to the fingerprint. A C# source is parsed and contributes its tokens, each with its length, and of
    /// its trivia only what changes the compilation: the directives with their own tokens, the text of a disabled <c>#if</c> branch,
    /// the message of <c>#region</c>, <c>#error</c> and <c>#warning</c>, skipped tokens and conflict markers. Comments, documentation
    /// comments included, and whitespace are left out. A disabled branch is text to the parser, so a comment inside one still counts.
    /// No preprocessor symbol is defined, so the code under <c>#if</c> counts as tokens or as disabled text but always counts.
    /// </summary>
    /// <param name="path">The input's full path.</param>
    /// <param name="source">Whether the input is a C# source, which contributes its tokens rather than its bytes.</param>
    /// <returns>The bytes the input contributes.</returns>
    private static byte[] Content(string path, bool source)
    {
        var content = File.ReadAllBytes(path);
        var length = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
                continue;
            content[length++] = content[index];
        }
        if (!source)
        {
            Array.Resize(ref content, length);
            return content;
        }

        string text;
        using (var reader = new StreamReader(new MemoryStream(content, 0, length), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = reader.ReadToEnd();
        using (var tokens = new MemoryStream())
        {
            foreach (var token in CSharpSyntaxTree.ParseText(text, SourceOptions).GetRoot().DescendantTokens())
                WriteToken(tokens, token);
            return tokens.ToArray();
        }
    }

    /// <summary>Writes a token's text and the trivia around it that changes the compilation.</summary>
    /// <param name="stream">The stream the input's contribution is written to.</param>
    /// <param name="token">The token to write.</param>
    private static void WriteToken(Stream stream, SyntaxToken token)
    {
        foreach (var trivia in token.LeadingTrivia)
            WriteTrivia(stream, trivia);
        WritePiece(stream, token.Text);
        foreach (var trivia in token.TrailingTrivia)
            WriteTrivia(stream, trivia);
    }

    /// <summary>
    /// Writes a trivia that changes the compilation and skips the rest. With <see cref="DocumentationMode.None"/> a documentation
    /// comment is an ordinary comment, so the only structured trivia are directives and skipped tokens.
    /// </summary>
    /// <param name="stream">The stream the input's contribution is written to.</param>
    /// <param name="trivia">The trivia to write or skip.</param>
    private static void WriteTrivia(Stream stream, SyntaxTrivia trivia)
    {
        if (trivia.GetStructure() is SyntaxNode structure)
        {
            foreach (var token in structure.DescendantTokens())
                WriteToken(stream, token);
        }
        else if (trivia.IsKind(SyntaxKind.DisabledTextTrivia) || trivia.IsKind(SyntaxKind.PreprocessingMessageTrivia) ||
                 trivia.IsKind(SyntaxKind.ConflictMarkerTrivia))
            WritePiece(stream, trivia.ToFullString());
    }

    /// <summary>Writes one piece of text after its length, so that two tokens never read as one.</summary>
    /// <param name="stream">The stream the input's contribution is written to.</param>
    /// <param name="text">The text of a token or of a trivia that counts.</param>
    private static void WritePiece(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
        stream.Write(bytes, 0, bytes.Length);
    }
}

#if !GENERATOR_FINGERPRINT_TESTS
public sealed class ComputeGeneratorFingerprint : Microsoft.Build.Utilities.Task
{
    [Microsoft.Build.Framework.Required]
    public string PluginsRoot { get; set; } = "";

    [Microsoft.Build.Framework.Output]
    public string Fingerprint { get; set; } = "";

    public override bool Execute()
    {
        Fingerprint = GeneratorFingerprint.Compute(PluginsRoot);
        return true;
    }
}
#endif
