using PlanForgeFlow.Serialization;

namespace PlanForgeFlow.Cli;

internal sealed class CliFailure : Exception
{
    public CliFailure(string code, string message, int exitCode = 1, IReadOnlyList<WithheldFile>? details = null)
        : base(message)
    {
        Code = code;
        ExitCode = exitCode;
        Details = details;
    }

    public string Code { get; }
    public int ExitCode { get; }
    public IReadOnlyList<WithheldFile>? Details { get; }
}
