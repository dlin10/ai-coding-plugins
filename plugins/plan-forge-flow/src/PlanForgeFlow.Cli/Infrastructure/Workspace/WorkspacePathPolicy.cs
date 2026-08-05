namespace PlanForgeFlow.Infrastructure.Workspace;

internal static class WorkspacePathPolicy
{
    public static StringComparison Comparison => OperatingSystem.IsWindows()
                                                     ? StringComparison.OrdinalIgnoreCase
                                                     : StringComparison.Ordinal;
}
