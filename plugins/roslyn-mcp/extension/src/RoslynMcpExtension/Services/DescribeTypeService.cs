using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

/// <summary>A type description (CONTEXT.md): what a caller in the project context can use on a type.</summary>
internal class DescribeTypeService(DocumentFinder documentFinder, TimeSpan budget)
{
	public const int BudgetSeconds = 45;

	private static readonly SymbolDisplayFormat Signature = SymbolDisplayFormat.MinimallyQualifiedFormat
		.RemoveMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType)
		.AddMemberOptions(SymbolDisplayMemberOptions.IncludeModifiers)
		.AddParameterOptions(SymbolDisplayParameterOptions.IncludeExtensionThis);

	public async Task<TypeDescriptionResult> DescribeTypeAsync(string? filePath, int line, int column, string? symbolId,
	                                                           string? projectName, string? memberFilter, int maxResults)
	{
		var result = new TypeDescriptionResult();
		var clock = Stopwatch.StartNew();

		try
		{
			var resolved = await new SymbolResolver(documentFinder).ResolveAsync(filePath, line, column, symbolId, projectName);
			result.Compilation = resolved.Compilation;
			var symbol = resolved.Required;
			var type = TypeOf(symbol) as INamedTypeSymbol
			           ?? throw new ToolRequestException(ToolErrorCodes.InvalidArgument,
			                                             $"'{symbol.ToDisplayString()}' has no named type to describe.");
			var compilation = await resolved.Project.GetCompilationAsync()
			                  ?? throw new InvalidOperationException("Failed to get compilation");

			result.Type = CodeMemberInfoFactory.Create(type, type.Name, "type", type.Locations.FirstOrDefault(l => l.IsInSource));
			for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
				result.BaseTypes.Add(CodeMemberInfoFactory.Create(baseType, baseType.Name, "type"));
			result.Interfaces.AddRange(type.AllInterfaces.Select(i => CodeMemberInfoFactory.Create(i, i.Name, "interface")));

			var members = type.GetMembers()
				.Where(IsApi)
				.Where(member => IsAccessible(member, compilation) && Matches(member.Name, memberFilter))
				.OrderBy(member => KindRank(member))
				.ThenBy(member => member.Name, StringComparer.Ordinal)
				.ToList();
			result.MemberCount = members.Count;
			result.Members.AddRange(members.Take(maxResults).Select(member => Describe(member, member)));

			var extensions = Extensions(type, compilation, memberFilter, clock, result.UnscannedAssemblies);
			result.ExtensionsComplete = result.UnscannedAssemblies.Count == 0;
			result.ExtensionCount = extensions.Count;
			// Those a caller at the position reaches as the code stands come first; then those nearest the type's own
			// namespace, where .NET convention puts the extensions meant for it.
			var inScope = InScopeIds(resolved.Site, type);
			var typeNamespace = type.ContainingNamespace.ToDisplayString();
			result.Extensions.AddRange(extensions
				.OrderBy(extension => inScope.Contains(CodeMemberInfoFactory.SymbolIdOf(extension.Definition)) ? 0 : 1)
				.ThenByDescending(extension => SharedSegments(extension.Definition.ContainingNamespace.ToDisplayString(), typeNamespace))
				.ThenBy(extension => extension.Definition.ContainingNamespace.ToDisplayString(), StringComparer.Ordinal)
				.ThenBy(extension => extension.Definition.Name, StringComparer.Ordinal)
				.Take(Math.Max(0, maxResults - result.Members.Count))
				.Select(extension =>
				{
					var member = Describe(extension.Reduced, extension.Definition);
					member.Namespace = extension.Definition.ContainingNamespace.ToDisplayString();
					member.Assembly = $"{extension.Definition.ContainingAssembly.Identity.Name} {extension.Definition.ContainingAssembly.Identity.Version}";
					return member;
				}));
			result.Truncated = result.Members.Count < result.MemberCount || result.Extensions.Count < result.ExtensionCount;
		}
		catch (Exception ex)
		{
			ToolResultErrors.Set(result, ex);
		}

		return result;
	}

	/// <summary>The type a position or ID stands for: a type itself, a variable's or member's type, a method's return type.</summary>
	private static ITypeSymbol? TypeOf(ISymbol symbol) => symbol switch
	{
		ITypeSymbol type => type,
		ILocalSymbol local => local.Type,
		IParameterSymbol parameter => parameter.Type,
		IFieldSymbol field => field.Type,
		IPropertySymbol property => property.Type,
		IEventSymbol @event => @event.Type,
		IMethodSymbol { MethodKind: MethodKind.Constructor } constructor => constructor.ContainingType,
		IMethodSymbol method => method.ReturnType,
		IDiscardSymbol discard => discard.Type,
		IAliasSymbol alias => alias.Target as ITypeSymbol,
		_ => null
	};

	/// <summary>A member a caller writes code against, not an accessor, a backing field or a static constructor.</summary>
	private static bool IsApi(ISymbol member) => member switch
	{
		IMethodSymbol { MethodKind: MethodKind.Constructor } => true,
		IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.UserDefinedOperator or MethodKind.Conversion } => !member.IsImplicitlyDeclared,
		IMethodSymbol => false,
		_ => !member.IsImplicitlyDeclared && member.CanBeReferencedByName
	};

	/// <summary>Callable from the project context, or protected and so usable by a type deriving from it.</summary>
	private static bool IsAccessible(ISymbol member, Compilation compilation)
		=> compilation.IsSymbolAccessibleWithin(member, compilation.Assembly)
		   || member.DeclaredAccessibility is Accessibility.Protected or Accessibility.ProtectedOrInternal;

	private static int KindRank(ISymbol member) => member switch
	{
		IMethodSymbol { MethodKind: MethodKind.Constructor } => 0,
		IPropertySymbol => 1,
		IMethodSymbol => 2,
		IEventSymbol => 3,
		IFieldSymbol => 4,
		_ => 5
	};

	private static TypeMember Describe(ISymbol shown, ISymbol definition) => new()
	{
		Name = definition.Name,
		Kind = CodeMemberInfoFactory.GetMemberType(definition),
		Signature = shown.ToDisplayString(Signature),
		SymbolId = CodeMemberInfoFactory.SymbolIdOf(definition),
		Accessibility = definition.DeclaredAccessibility.ToString(),
		Summary = Summary(definition),
		Obsolete = definition.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.ObsoleteAttribute") ? true : null
	};

	/// <summary>
	/// The extension methods every assembly the project sees declares for the type, scanned one assembly at a time
	/// until the request's budget runs out (docs/adr/0002). Receivers that accept any type are left out.
	/// </summary>
	private List<(IMethodSymbol Reduced, IMethodSymbol Definition)> Extensions(INamedTypeSymbol type, Compilation compilation,
	                                                                          string? memberFilter, Stopwatch clock, List<string> unscanned)
	{
		var found = new List<(IMethodSymbol, IMethodSymbol)>();
		foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols.Prepend(compilation.Assembly))
		{
			if (clock.Elapsed >= budget)
			{
				unscanned.Add(assembly.Identity.Name);
				continue;
			}

			foreach (var container in ExtensionContainers(assembly.GlobalNamespace))
			{
				foreach (var method in container.GetMembers().OfType<IMethodSymbol>())
				{
					if (method.IsExtensionMethod && !AcceptsAnyType(method.Parameters[0].Type) && Matches(method.Name, memberFilter)
					    && compilation.IsSymbolAccessibleWithin(method, compilation.Assembly)
					    && method.ReduceExtensionMethod(type) is { } reduced)
						found.Add((reduced, method));
				}
			}
		}

		return found;
	}

	private static IEnumerable<INamedTypeSymbol> ExtensionContainers(INamespaceSymbol @namespace)
	{
		foreach (var member in @namespace.GetMembers())
		{
			if (member is INamespaceSymbol child)
			{
				foreach (var container in ExtensionContainers(child))
					yield return container;
			}
			else if (member is INamedTypeSymbol { IsStatic: true, MightContainExtensionMethods: true } container)
			{
				yield return container;
			}
		}
	}

	/// <summary>The IDs of the extension methods a caller at the site reaches with no using to add; none without a site.</summary>
	private static HashSet<string?> InScopeIds((SemanticModel Model, int Position)? site, INamedTypeSymbol type)
		=> site is { } at
			? at.Model.LookupSymbols(at.Position, type, includeReducedExtensionMethods: true)
				.OfType<IMethodSymbol>()
				.Where(method => method.ReducedFrom != null)
				.Select(CodeMemberInfoFactory.SymbolIdOf)
				.ToHashSet()
			: [];

	/// <summary>How many leading namespace segments two namespaces share: Microsoft.AspNetCore and Microsoft.Extensions share one.</summary>
	private static int SharedSegments(string first, string second)
	{
		var a = first.Split('.');
		var b = second.Split('.');
		var shared = 0;
		while (shared < a.Length && shared < b.Length && a[shared] == b[shared])
			shared++;
		return shared;
	}

	private static bool AcceptsAnyType(ITypeSymbol receiver)
		=> receiver.SpecialType == SpecialType.System_Object || receiver is ITypeParameterSymbol { ConstraintTypes.IsEmpty: true };

	/// <summary>
	/// A case-insensitive substring of the name, or prefixes of humps that follow one another in it: TGA finds
	/// TryGetAsync and AddSin finds AddSingleton, but AddSingleton does not find AddDiSingleton.
	/// </summary>
	internal static bool Matches(string name, string? filter)
	{
		if (string.IsNullOrEmpty(filter) || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
			return true;

		var humps = Humps(name);
		var pieces = Humps(filter!);
		for (var start = 0; start + pieces.Count <= humps.Count; start++)
		{
			if (pieces.Select((piece, i) => humps[start + i].StartsWith(piece, StringComparison.OrdinalIgnoreCase)).All(match => match))
				return true;
		}

		return false;
	}

	private static List<string> Humps(string text)
		=> Regex.Split(text, "(?<!^)(?=[A-Z])").Where(hump => hump.Length > 0).ToList();

	/// <summary>The summary from the XML documentation, flattened to one line, with references reduced to their names.</summary>
	private static string? Summary(ISymbol symbol)
	{
		var xml = symbol.GetDocumentationCommentXml();
		if (string.IsNullOrWhiteSpace(xml))
			return null;

		try
		{
			var summary = XElement.Parse(xml).Element("summary");
			return summary == null ? null : Regex.Replace(string.Concat(summary.Nodes().Select(Text)), @"\s+", " ").Trim();
		}
		catch (XmlException)
		{
			return null;
		}

		static string Text(XNode node) => node switch
		{
			XText text => text.Value,
			XElement element when element.Attribute("cref") is { } cref => cref.Value.Substring(cref.Value.IndexOf(':') + 1),
			XElement element when (element.Attribute("name") ?? element.Attribute("langword")) is { } name => name.Value,
			XElement element => string.Concat(element.Nodes().Select(Text)),
			_ => string.Empty
		};
	}
}
