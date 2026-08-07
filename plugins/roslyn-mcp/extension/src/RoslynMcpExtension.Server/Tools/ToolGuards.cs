using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Server.Tools;

internal static class ToolGuards
{
	public static bool HasInvalidPosition(int line, int column)
	{
		return line < 1 || column < 1;
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
