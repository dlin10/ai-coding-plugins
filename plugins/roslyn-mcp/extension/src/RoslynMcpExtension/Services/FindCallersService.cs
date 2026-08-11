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
	public async Task<SymbolListResult> FindCallersAsync(string filePath, int line, int column, int maxResults)
	{
		var result = new SymbolListResult();

		try
		{
			var document = documentFinder.FindDocument(filePath);
			var semanticModel = await document.GetSemanticModelAsync();
			var syntaxTree = await document.GetSyntaxTreeAsync();
			if (semanticModel == null || syntaxTree == null)
			{
				result.ErrorMessage = "Failed to get semantic model";
				return result;
			}

			result.Compilation = DocumentFinder.CreateCompilationInfo(document, semanticModel);

			var position = DocumentFinder.GetPosition(syntaxTree, line, column);
			var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel,
			                                                              position,
			                                                              documentFinder.Workspace);
			if (symbol == null)
			{
				throw new ToolRequestException(ToolErrorCodes.InvalidArgument,
				                               $"No symbol found at line {line}, column {column}");
			}

			result.Symbol = CodeMemberInfoFactory.Create(symbol,
			                                                   symbol.Name,
			                                                   "member",
			                                                   symbol.Locations.FirstOrDefault(location => location.IsInSource),
			                                                   document.Project.Name);

			if (symbol is not IMethodSymbol and not IPropertySymbol and not IEventSymbol)
			{
				result.ErrorMessage = $"Symbol '{symbol.ToDisplayString()}' is not callable.";
				return result;
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
					var projectName = location.SourceTree == null
						? null
						: solution.GetDocument(location.SourceTree)?.Project.Name;
					var member = CodeMemberInfoFactory.Create(caller.CallingSymbol,
					                                                  displayName,
					                                                  caller.IsDirect ? "caller" : "indirect-caller",
					                                                  location,
					                                                  projectName);
					member.Name = displayName;
					member.MemberType = caller.IsDirect ? "caller" : "indirect-caller";
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
