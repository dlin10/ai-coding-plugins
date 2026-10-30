using System;
using System.Threading;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Services;

internal sealed class ToolRequestException(string code, string message) : Exception(message)
{
	public string Code { get; } = code;
}

internal static class ToolResultErrors
{
	public static void Set(IToolResult result, Exception exception)
	{
		switch (exception)
		{
			case ToolRequestException requestException:
				result.ErrorCode = requestException.Code;
				result.ErrorMessage = requestException.Message;
				break;
			case OperationCanceledException:
				result.ErrorCode = ToolErrorCodes.Cancelled;
				result.ErrorMessage = "The request was cancelled.";
				break;
			default:
				result.ErrorCode = ToolErrorCodes.InternalError;
				result.ErrorMessage = exception.Message;
				break;
		}
	}
}
