using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

internal class ImplementationsService(DocumentFinder documentFinder)
{
	public async Task<SymbolListResult> FindImplementationsAsync(string? filePath, int line, int column, string? symbolId,
	                                                             string? projectName, int maxResults)
	{
		var result = new SymbolListResult();

		try
		{
			var resolved = await new SymbolResolver(documentFinder).ResolveAsync(filePath, line, column, symbolId, projectName);
			result.Compilation = resolved.Compilation;
			var symbol = resolved.Required;

			result.Symbol = CodeMemberInfoFactory.Create(symbol,
			                                                   symbol.Name,
			                                                   "member",
			                                                   symbol.Locations.FirstOrDefault(l => l.IsInSource),
			                                                   resolved.Project.Name);

			var solution = documentFinder.Workspace.CurrentSolution;
			var relationships = new List<(ISymbol Symbol, string MemberType)>();

			switch (symbol)
			{
				case INamedTypeSymbol { TypeKind: TypeKind.Interface } interfaceType:
					var implementations = await SymbolFinder.FindImplementationsAsync(interfaceType,
					                                                                         solution,
					                                                                         cancellationToken: CancellationToken.None);
					relationships.AddRange(implementations.Select(implementation => ((ISymbol)implementation, "implementation")));

					var derivedInterfaces = await SymbolFinder.FindDerivedInterfacesAsync(interfaceType,
					                                                                            solution,
					                                                                            cancellationToken: CancellationToken.None);
					relationships.AddRange(derivedInterfaces.Select(derivedInterface => ((ISymbol)derivedInterface, "derived-interface")));
					break;

				case INamedTypeSymbol { TypeKind: TypeKind.Class } classType:
					var derivedClasses = await SymbolFinder.FindDerivedClassesAsync(classType,
					                                                                      solution,
					                                                                      cancellationToken: CancellationToken.None);
					relationships.AddRange(derivedClasses.Select(derivedClass => ((ISymbol)derivedClass, "derived-class")));
					break;

				case { ContainingType.TypeKind: TypeKind.Interface }:
					var memberImplementations = await SymbolFinder.FindImplementationsAsync(symbol,
					                                                                               solution,
					                                                                               cancellationToken: CancellationToken.None);
					relationships.AddRange(memberImplementations.Select(implementation => (implementation, "implementation")));
					break;

				case IMethodSymbol { IsVirtual: true } or IMethodSymbol { IsAbstract: true }:
				case IPropertySymbol { IsVirtual: true } or IPropertySymbol { IsAbstract: true }:
				case IEventSymbol { IsVirtual: true } or IEventSymbol { IsAbstract: true }:
					var overrides = await SymbolFinder.FindOverridesAsync(symbol,
					                                                              solution,
					                                                              cancellationToken: CancellationToken.None);
					relationships.AddRange(overrides.Select(@override => (@override, "override")));
					break;

				default:
					result.ErrorMessage = $"Symbol '{symbol.ToDisplayString()}' cannot have implementations, derived types, or overrides.";
					return result;
			}

			foreach (var relationship in relationships)
			{
				foreach (var location in relationship.Symbol.Locations.Where(location => location.IsInSource))
				{
					if (result.Members.Count >= maxResults) break;

					var locationProject = location.SourceTree == null
						? null
						: solution.GetDocument(location.SourceTree)?.Project.Name;
					var member = CodeMemberInfoFactory.Create(relationship.Symbol,
					                                                  relationship.Symbol.Name,
					                                                  relationship.MemberType,
					                                                  location,
					                                                  locationProject);
					member.MemberType = relationship.MemberType;
					result.Members.Add(member);
				}

				if (result.Members.Count >= maxResults) break;
			}

			result.TotalCount = result.Members.Count;
			result.Truncated = result.Members.Count >= maxResults;
		}
		catch (Exception ex)
		{
			ToolResultErrors.Set(result, ex);
		}

		return result;
	}
}
