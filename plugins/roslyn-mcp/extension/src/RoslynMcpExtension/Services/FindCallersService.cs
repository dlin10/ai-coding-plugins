using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

internal class FindCallersService(DocumentFinder documentFinder)
{
	public async Task<SymbolListResult> FindCallersAsync(string? filePath, int line, int column, string? symbolId,
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
			                                                   symbol.Locations.FirstOrDefault(location => location.IsInSource),
			                                                   resolved.Project.Name);

			if (symbol is not IMethodSymbol and not IPropertySymbol and not IEventSymbol)
			{
				throw new ToolRequestException(ToolErrorCodes.InvalidArgument,
				                               $"Symbol '{symbol.ToDisplayString()}' is not callable.");
			}

			var solution = documentFinder.Workspace.CurrentSolution;
			var callers = await SymbolFinder.FindCallersAsync(symbol,
			                                                    solution,
			                                                    CancellationToken.None);

			foreach (var caller in callers)
			{
				foreach (var location in caller.Locations.Where(location => location.IsInSource))
				{
					if (result.Members.Count >= maxResults) break;

					var displayName = caller.CallingSymbol.ToDisplayString();
					var locationProject = location.SourceTree == null
						? null
						: solution.GetDocument(location.SourceTree)?.Project.Name;
					var member = CodeMemberInfoFactory.Create(caller.CallingSymbol,
					                                                  displayName,
					                                                  caller.IsDirect ? "caller" : "indirect-caller",
					                                                  location,
					                                                  locationProject);
					member.Name = displayName;
					member.MemberType = caller.IsDirect ? "caller" : "indirect-caller";
					member.ContainingSymbolId = CodeMemberInfoFactory.NearestSymbolIdOf(caller.CallingSymbol);
					await CodeMemberInfoFactory.SetEnclosingSpanAsync(member, caller.CallingSymbol, location);
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
