using PlanForge.Infrastructure;

namespace PlanForge.Vendors;

internal sealed class ClaudeCliVendor : IVendor
{
    private const string Command = "claude";

    // Five levels, verified against the CLI. The old code carried six, including a "none" that
    // does not exist.
    private static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];

    private readonly string? _workingDirectory;

    public ClaudeCliVendor(string? workingDirectory = null) => _workingDirectory = workingDirectory;

    public string Id => "claude";

    // Claude is the one vendor without a live model list, so its catalog is declarative and
    // advisory: an unknown model is a warning, not a refusal.
    public VendorCatalog Catalog { get; } = new([
        new VendorModel("opus", Efforts),
        new VendorModel("sonnet", Efforts),
        new VendorModel("haiku", Efforts)
    ]);

    /// <summary>
    /// Resolved through the shim: on Windows `claude` is a .cmd, and running it would go through
    /// cmd.exe, which corrupts the inline JSON schema argument.
    /// </summary>
    internal static string Executable =>
        ExecutableResolver.Resolve(Command) ?? throw new VendorException($"{Command} was not found on PATH");

    public async Task<VendorReadiness> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var spec = new ProcessSpec(Executable, ["--version"], _workingDirectory, string.Empty);
            var lines = await StreamingProcess.CollectAsync(spec, TimeSpan.FromSeconds(30), ct);
            return new VendorReadiness(true, lines.FirstOrDefault() ?? "unknown version");
        }
        catch (Exception error) when (error is VendorException or OperationCanceledException)
        {
            return new VendorReadiness(false, error.Message);
        }
    }

    public Task<IVendorSession> StartAsync(RoleSpec role, Selection selection, string? resumeToken, CancellationToken ct) =>
        Task.FromResult<IVendorSession>(new ClaudeCliSession(role, selection, _workingDirectory, resumeToken));
}
