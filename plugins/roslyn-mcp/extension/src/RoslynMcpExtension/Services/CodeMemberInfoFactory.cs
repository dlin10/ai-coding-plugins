using System.Linq;
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
        info.SymbolId = SymbolIdOf(symbol);
        if (symbol.Locations.All(l => !l.IsInSource) && symbol.ContainingAssembly is { } assembly)
            info.Assembly = $"{assembly.Identity.Name} {assembly.Identity.Version}";

        return info;
    }

    /// <summary>
    /// The documentation comment ID a request can name the symbol by (docs/adr/0001), or null for the kinds that
    /// have none: locals, parameters, local functions, lambdas, anonymous types.
    /// </summary>
    public static string? SymbolIdOf(ISymbol? symbol)
    {
        // A generic instantiation and an extension method called on an instance both share their definition's ID.
        var definition = (symbol is IMethodSymbol { ReducedFrom: { } reducedFrom } ? reducedFrom : symbol)?.OriginalDefinition;
        var id = definition switch
        {
            IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction } => null,
            INamedTypeSymbol { IsAnonymousType: true } or INamespaceSymbol { IsGlobalNamespace: true } => null,
            INamespaceSymbol or INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol
                => DocumentationCommentId.CreateDeclarationId(definition),
            _ => null
        };

        // Roslyn appends "~ReturnType" to every method. The documented form, the one XML documentation files carry,
        // keeps it only on a conversion operator, the one kind C# overloads by return type; both forms resolve.
        var returnType = id?.IndexOf('~') ?? -1;
        return returnType >= 0 && definition is IMethodSymbol { MethodKind: not MethodKind.Conversion } ? id!.Substring(0, returnType) : id;
    }

    /// <summary>
    /// The ID of the nearest symbol that has one, walking out of lambdas and local functions to the member
    /// declaring them — the one a client asks about next when it follows a chain of calls.
    /// </summary>
    public static string? NearestSymbolIdOf(ISymbol? symbol)
    {
        for (; symbol != null; symbol = symbol.ContainingSymbol)
        {
            if (SymbolIdOf(symbol) is { } id)
                return id;
        }

        return null;
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
