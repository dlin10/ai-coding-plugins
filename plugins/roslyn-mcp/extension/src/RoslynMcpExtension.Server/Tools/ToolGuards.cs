using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

/// <summary>The parameters naming a symbol by position or by symbol ID (docs/adr/0001), described once for every tool taking them.</summary>
internal static class SymbolAddress
{
	public const string Summary = " Name the symbol by filePath, line and column, or by symbolId: the documentation comment ID every result carries, which stays valid when files are edited.";
	public const string FilePath = "Absolute path to the C# file; with line and column, names the symbol by position. Omit when passing symbolId";
	public const string Line = "Line number (1-based), with filePath";
	public const string Column = "Column number (1-based), with filePath";
	public const string SymbolId = "Documentation comment ID such as M:Ns.Type.Method(System.Int32), copied from a symbolId or containingSymbolId in an earlier result; instead of a position";
	public const string ProjectName = "Project to resolve symbolId in; by default the project declaring it, or for a symbol from metadata the first project referencing it";
}

internal static class ToolGuards
{
	public static bool HasInvalidPosition(int line, int column)
	{
		return line < 1 || column < 1;
	}

	/// <summary>A position is checked here only when one was passed; naming both or neither is the extension's to reject.</summary>
	public static bool HasInvalidPosition(string? filePath, int line, int column)
	{
		return filePath != null && HasInvalidPosition(line, column);
	}

	public static int ClampMaxResults(int maxResults, int maximum)
	{
		return maxResults < 1 ? 1 : maxResults > maximum ? maximum : maxResults;
	}

	public static SymbolListResult InvalidSymbolList(string message)
	{
		return new SymbolListResult
		{
			ErrorCode = ToolErrorCodes.InvalidArgument,
			ErrorMessage = message
		};
	}

	public static SymbolInfoResult InvalidSymbolInfo(string message)
	{
		return new SymbolInfoResult
		{
			ErrorCode = ToolErrorCodes.InvalidArgument,
			ErrorMessage = message
		};
	}
}
