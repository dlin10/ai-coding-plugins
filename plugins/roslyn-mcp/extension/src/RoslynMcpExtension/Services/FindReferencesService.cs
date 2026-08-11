using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

internal class FindReferencesService(DocumentFinder documentFinder)
{
	public async Task<SymbolListResult> FindReferencesAsync(string filePath, int line, int column, int maxResults)
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
			var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, documentFinder.Workspace);
			if (symbol == null)
			{
				throw new ToolRequestException(ToolErrorCodes.InvalidArgument,
				                               $"No symbol found at line {line}, column {column}");
			}

			result.Symbol = CodeMemberInfoFactory.Create(
				symbol,
				symbol.Name,
				"member",
				symbol.Locations.FirstOrDefault(l => l.IsInSource));

			var references = await SymbolFinder.FindReferencesAsync(symbol, documentFinder.Workspace.CurrentSolution);
			var locations = new List<SymbolLocation>();
			var solution = documentFinder.Workspace.CurrentSolution;

			foreach (var refSymbol in references)
			{
				foreach (var loc in refSymbol.Definition.Locations.Where(l => l.IsInSource))
				{
					locations.Add(await CreateLocationAsync(refSymbol.Definition, loc, "definition", solution));
				}

				foreach (var loc in refSymbol.Locations)
				{
					locations.Add(await CreateLocationAsync(refSymbol.Definition, loc.Location, "reference", solution));
				}
			}

			var orderedLocations = locations
				.GroupBy(location => (location.FilePath, location.StartLine, location.StartColumn))
				.Select(group => group.First())
				.OrderBy(location => location.FilePath, StringComparer.OrdinalIgnoreCase)
				.ThenBy(location => location.StartLine)
				.ThenBy(location => location.StartColumn)
				.ToList();

			result.TotalCount = orderedLocations.Count;
			result.Truncated = orderedLocations.Count > maxResults;
			result.Members.AddRange(orderedLocations.Take(maxResults));
		}
		catch (Exception ex)
		{
			ToolResultErrors.Set(result, ex);
		}

		return result;
	}

	private static async Task<SymbolLocation> CreateLocationAsync(ISymbol referencedSymbol,
	                                                              Location location,
	                                                              string memberType,
	                                                              Solution solution)
	{
		var lineSpan = location.GetLineSpan();
		var document = location.SourceTree == null ? null : solution.GetDocument(location.SourceTree);
		var semanticModel = document == null ? null : await document.GetSemanticModelAsync();
		var containingSymbol = semanticModel?.GetEnclosingSymbol(location.SourceSpan.Start);

		var info = new SymbolLocation
		{
			Name = referencedSymbol.Name,
			MemberType = memberType,
			ContainingSymbol = containingSymbol?.ToDisplayString(),
			ProjectName = document?.Project.Name,
			FilePath = location.SourceTree?.FilePath ?? string.Empty,
			StartLine = lineSpan.StartLinePosition.Line + 1,
			StartColumn = lineSpan.StartLinePosition.Character + 1,
			EndLine = lineSpan.EndLinePosition.Line + 1
		};

		// On a definition the position sits on the declaration's identifier, whose enclosing symbol is the
		// containing type; the declared symbol itself is the useful span there.
		var spanSymbol = memberType == "definition" ? referencedSymbol : containingSymbol;
		await CodeMemberInfoFactory.SetEnclosingSpanAsync(info, spanSymbol, location);

		return info;
	}
}
