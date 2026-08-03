using PlanForgeFlow.Cli;
using PlanForgeFlow.Workflow.State;

namespace PlanForgeFlow.Infrastructure.Process;

internal sealed class GitClient
{
    private readonly string _workspace;

    public GitClient(string workspace)
    {
        _workspace = workspace;
    }

    public ProcessResult Run(IReadOnlyList<string> args)
        => ProcessExecution.Run("git", ["-C", _workspace, .. args]);

    public string Output(IReadOnlyList<string> args, string errorMessage)
    {
        var result = Run(args);
        if (result.ExitCode != 0) throw new CliFailure("environment", $"{errorMessage}: {result.Stderr.Trim()}");
        return result.Stdout;
    }

    public string[] NullSeparatedPaths(IReadOnlyList<string> args, string errorMessage)
        => Output(args, errorMessage).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    public string DiffOutput(IReadOnlyList<string> args, IReadOnlyList<string> paths, string errorMessage)
        => paths.Count == 0 ? string.Empty : Output([.. args, "--", .. paths], errorMessage);
}
