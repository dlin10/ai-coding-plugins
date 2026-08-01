using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal sealed class CliFailure : Exception
{
    public CliFailure(string code, string message, int exitCode = 1, JsonNode? details = null)
        : base(message)
    {
        Code = code;
        ExitCode = exitCode;
        Details = details;
    }

    public string Code { get; }
    public int ExitCode { get; }
    public JsonNode? Details { get; }
}