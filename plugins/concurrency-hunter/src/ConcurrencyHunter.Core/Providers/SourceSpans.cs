using ConcurrencyHunter.Analysis;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers;

internal static class SourceSpans
{
    /// <summary>A one-based span whose path is relative to <paramref name="rootDirectory"/> with <c>/</c>, or absolute
    /// when the file lies outside it.</summary>
    internal static SourceSpan From(Location location, string rootDirectory)
    {
        var lineSpan = location.GetLineSpan();
        var fullPath = Path.GetFullPath(lineSpan.Path);
        var relativePath = Path.GetRelativePath(Path.GetFullPath(rootDirectory), fullPath);
        var outsideRoot = relativePath == ".." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                          Path.IsPathRooted(relativePath);
        var path = (outsideRoot ? fullPath : relativePath).Replace('\\', '/');
        return new SourceSpan(path, lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1,
                              lineSpan.EndLinePosition.Line + 1, lineSpan.EndLinePosition.Character + 1);
    }

    internal static SourceSpan From(SyntaxNode node, string rootDirectory) => From(node.GetLocation(), rootDirectory);
}
