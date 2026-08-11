using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

internal static class CodeMemberInfoFactory
{
    public static SymbolLocation Create(
        ISymbol? symbol,
        string fallbackName,
        string fallbackMemberType,
        Location? location = null,
        string? projectName = null)
    {
        var info = new SymbolLocation
        {
            Name = fallbackName,
            FullName = fallbackName,
            MemberType = fallbackMemberType,
            ProjectName = projectName
        };

        if (location?.IsInSource == true)
        {
            var lineSpan = location.GetLineSpan();
            info.FilePath = location.SourceTree?.FilePath ?? string.Empty;
            info.StartLine = lineSpan.StartLinePosition.Line + 1;
            info.StartColumn = lineSpan.StartLinePosition.Character + 1;
            info.EndLine = lineSpan.EndLinePosition.Line + 1;
        }

        if (symbol == null)
            return info;

        if (string.IsNullOrWhiteSpace(info.Name))
            info.Name = symbol.Name;

        info.FullName = symbol.ToDisplayString();
        info.MemberType = GetMemberType(symbol);
        info.Accessibility = symbol.DeclaredAccessibility.ToString();

        return info;
    }

    /// <summary>
    /// Records the declaration span of the member that encloses <paramref name="location"/>, so callers can read
    /// that member's source directly instead of listing the whole document's symbols to find its boundaries.
    /// </summary>
    public static async Task SetEnclosingSpanAsync(SymbolLocation info, ISymbol? enclosingSymbol, Location location)
    {
        for (var symbol = enclosingSymbol; symbol != null && symbol is not INamespaceSymbol; symbol = symbol.ContainingSymbol)
        {
            // A lambda's own span is too narrow to read as context; report the member that declares it.
            if (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction })
                continue;

            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                // Partial types and overloads can declare the same symbol elsewhere; keep the declaration we are inside of.
                if (reference.SyntaxTree != location.SourceTree || !reference.Span.Contains(location.SourceSpan.Start))
                    continue;

                var syntax = await reference.GetSyntaxAsync();
                var lineSpan = syntax.GetLocation().GetLineSpan();
                info.EnclosingStartLine = lineSpan.StartLinePosition.Line + 1;
                info.EnclosingEndLine = lineSpan.EndLinePosition.Line + 1;
                return;
            }
        }
    }

    public static SymbolInfoResult CreateSymbolInfo(
        ISymbol symbol,
        Location? location = null)
    {
        return new SymbolInfoResult
        {
            Symbol = Create(symbol, symbol.Name, GetMemberType(symbol), location),
            Detail = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            Documentation = symbol.GetDocumentationCommentXml()
        };
    }

    public static string GetMemberType(ISymbol symbol)
    {
        return symbol switch
        {
            INamespaceSymbol => "namespace",
            INamedTypeSymbol { IsRecord: true } => "record",
            INamedTypeSymbol typeSymbol => typeSymbol.TypeKind switch
            {
                TypeKind.Class => "class",
                TypeKind.Struct => "struct",
                TypeKind.Interface => "interface",
                TypeKind.Enum => "enum",
                TypeKind.Delegate => "delegate",
                _ => "type"
            },
            IMethodSymbol { MethodKind: MethodKind.Constructor } => "constructor",
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            IParameterSymbol => "parameter",
            ILocalSymbol => "local",
            _ => symbol.Kind.ToString().ToLowerInvariant()
        };
    }
}
