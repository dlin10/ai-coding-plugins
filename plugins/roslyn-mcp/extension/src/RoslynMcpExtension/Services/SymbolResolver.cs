using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

/// <summary>The symbol a request named, and the project context it was resolved in.</summary>
internal sealed class ResolvedSymbol(ISymbol? symbol, Project project, CompilationInfo compilation, string notFound,
                                     (SemanticModel Model, int Position)? site = null)
{
	public ISymbol? Symbol { get; } = symbol;
	public Project Project { get; } = project;
	public CompilationInfo Compilation { get; } = compilation;

	/// <summary>Where a request named by position points; null for one named by symbol ID.</summary>
	public (SemanticModel Model, int Position)? Site { get; } = site;

	/// <summary>The symbol, or an invalid_argument naming the position that held none.</summary>
	public ISymbol Required => Symbol ?? throw new ToolRequestException(ToolErrorCodes.InvalidArgument, notFound);
}

/// <summary>Turns a position or a symbol ID (docs/adr/0001) into a symbol and its project context.</summary>
internal class SymbolResolver(DocumentFinder documentFinder)
{
	public async Task<ResolvedSymbol> ResolveAsync(string? filePath, int line, int column, string? symbolId, string? projectName)
	{
		if (filePath != null && symbolId != null)
			throw new ToolRequestException(ToolErrorCodes.InvalidArgument, "Pass a position or a symbolId, not both.");
		if (filePath == null && symbolId == null)
			throw new ToolRequestException(ToolErrorCodes.InvalidArgument, "Pass filePath with line and column, or a symbolId.");

		return symbolId != null
			? await ResolveIdAsync(symbolId, projectName)
			: await ResolvePositionAsync(filePath!, line, column);
	}

	private async Task<ResolvedSymbol> ResolvePositionAsync(string filePath, int line, int column)
	{
		var document = documentFinder.FindDocument(filePath);
		var semanticModel = await document.GetSemanticModelAsync();
		var syntaxTree = await document.GetSyntaxTreeAsync();
		if (semanticModel == null || syntaxTree == null)
			throw new InvalidOperationException("Failed to get semantic model");

		var position = DocumentFinder.GetPosition(syntaxTree, line, column);
		var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, documentFinder.Workspace);
		return new ResolvedSymbol(symbol, document.Project, DocumentFinder.CreateCompilationInfo(document, semanticModel),
		                          $"No symbol found at line {line}, column {column}", (semanticModel, position));
	}

	private async Task<ResolvedSymbol> ResolveIdAsync(string symbolId, string? projectName)
	{
		var solution = documentFinder.Workspace.CurrentSolution;
		if (!solution.Projects.Any())
		{
			throw new ToolRequestException(ToolErrorCodes.WorkspaceNotReady,
			                               "No solution is loaded or the workspace is still initializing.");
		}

		// A symbol declared in source resolves in the project declaring it, found through the declaration index
		// without compiling anything.
		if (projectName == null && SimpleName(symbolId) is { } name)
		{
			foreach (var declaration in await SymbolFinder.FindSourceDeclarationsAsync(solution, name, ignoreCase: false))
			{
				if (CodeMemberInfoFactory.SymbolIdOf(declaration) == symbolId
				    && solution.GetProject(declaration.ContainingAssembly) is { } declaring)
					return await InProjectAsync(declaring, declaration);
			}
		}

		// Otherwise the first project whose compilation resolves it: the one named, or any for metadata.
		var projects = projectName == null ? solution.Projects : DocumentFinder.ProjectsNamed(solution, projectName);
		foreach (var project in projects)
		{
			var symbol = await ResolveInAsync(project, symbolId);
			if (symbol == null)
				continue;

			// A source symbol reached through a project reference belongs to the project declaring it.
			if (projectName == null && symbol.Locations.Any(location => location.IsInSource)
			    && solution.GetProject(symbol.ContainingAssembly) is { } declaring && declaring.Id != project.Id
			    && await ResolveInAsync(declaring, symbolId) is { } declared)
				return await InProjectAsync(declaring, declared);

			return await InProjectAsync(project, symbol);
		}

		var scope = projectName == null ? $"any project of solution {solution.FilePath}" : $"project '{projectName}'";
		throw new ToolRequestException(ToolErrorCodes.InvalidArgument, $"No symbol with ID '{symbolId}' in {scope}.");
	}

	private static async Task<ISymbol?> ResolveInAsync(Project project, string symbolId)
	{
		var compilation = await project.GetCompilationAsync();
		return compilation == null ? null : DocumentationCommentId.GetFirstSymbolForDeclarationId(symbolId, compilation);
	}

	private static async Task<ResolvedSymbol> InProjectAsync(Project project, ISymbol symbol)
	{
		var source = symbol.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree;
		var document = source == null ? null : project.Solution.GetDocument(source);
		var semanticModel = document == null ? null : await document.GetSemanticModelAsync();
		return new ResolvedSymbol(symbol, project, DocumentFinder.CreateCompilationInfo(project, semanticModel), string.Empty);
	}

	/// <summary>
	/// The name a declaration index knows the symbol by: <c>M:Ns.Type.Method(System.Int32)</c> gives
	/// <c>Method</c>. Null for an ID whose symbol is named differently in source — a constructor, an indexer, an
	/// explicit implementation — which then resolves through compilations instead.
	/// </summary>
	private static string? SimpleName(string symbolId)
	{
		if (symbolId.Length < 3 || symbolId[1] != ':')
			return null;

		var path = symbolId.Substring(2);
		var parameters = path.IndexOf('(');
		if (parameters >= 0)
			path = path.Substring(0, parameters);
		var name = path.Substring(path.LastIndexOf('.') + 1);
		var arity = name.IndexOf('`');
		if (arity >= 0)
			name = name.Substring(0, arity);
		return name.Length == 0 || name.IndexOf('#') >= 0 || name == "Item" ? null : name;
	}
}
