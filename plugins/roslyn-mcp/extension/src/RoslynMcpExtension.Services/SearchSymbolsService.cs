using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

internal class SearchSymbolsService(DocumentFinder documentFinder)
{
	public async Task<SymbolListResult> SearchSymbolsAsync(string query, bool includeMetadata, int maxResults)
	{
		var result = new SymbolListResult();

		try
		{
			var solution = documentFinder.Workspace.CurrentSolution;
			if (!solution.Projects.Any())
			{
				throw new ToolRequestException(ToolErrorCodes.WorkspaceNotReady,
			                               "No solution is loaded or the workspace is still initializing.");
			}

			foreach (var project in solution.Projects)
			{
				if (result.Members.Count >= maxResults) break;

				var symbols = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(project,
				                                                                    query,
				                                                                    SymbolFilter.TypeAndMember,
				                                                                    CancellationToken.None);

				foreach (var symbol in symbols)
				{
					if (result.Members.Count >= maxResults) break;
					if (symbol.Locations.Length == 0) continue;

					var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
					if (loc == null) continue;

					var member = CodeMemberInfoFactory.Create(symbol, symbol.Name, "member", loc, project.Name);
					await CodeMemberInfoFactory.SetEnclosingSpanAsync(member, symbol, loc);
					result.Members.Add(member);
				}
			}

			if (includeMetadata)
				await AddMetadataDeclarationsAsync(solution, query, maxResults, result);

			result.TotalCount = result.Members.Count;
			result.Truncated = result.Members.Count >= maxResults;
		}
		catch (Exception ex)
		{
			ToolResultErrors.Set(result, ex);
		}

		return result;
	}

	/// <summary>
	/// Types and members from referenced assemblies whose name is exactly the query: a pattern over every
	/// referenced assembly would be far too slow. Each is reported once, from the first project referencing it.
	/// </summary>
	private static async Task AddMetadataDeclarationsAsync(Solution solution, string query, int maxResults, SymbolListResult result)
	{
		var seen = new HashSet<string>();
		foreach (var project in solution.Projects)
		{
			if (result.Members.Count >= maxResults) break;

			var declarations = await SymbolFinder.FindDeclarationsAsync(project, query, ignoreCase: false,
			                                                            SymbolFilter.TypeAndMember, CancellationToken.None);
			foreach (var symbol in declarations.Where(symbol => symbol.Locations.All(location => location.IsInMetadata)))
			{
				if (result.Members.Count >= maxResults) break;

				var member = CodeMemberInfoFactory.Create(symbol, symbol.Name, "member", projectName: project.Name);
				if (seen.Add(member.SymbolId + "|" + member.Assembly))
					result.Members.Add(member);
			}
		}
	}
}
