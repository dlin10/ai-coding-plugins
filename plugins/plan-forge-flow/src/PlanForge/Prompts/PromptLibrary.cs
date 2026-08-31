using PlanForge.Vendors;

namespace PlanForge.Prompts;

/// <summary>
/// Role prompts live as files under <c>prompts/&lt;vendor&gt;/&lt;role&gt;.md</c> so they can be
/// edited and tuned per project without rebuilding the binary.
/// </summary>
internal sealed class PromptLibrary(string? root = null)
{
    private const string PromptsFolder = "prompts";
    private const string RoslynContractFile = "roslyn-contract.md";
    private const string ScopeContractFile = "scope-contract.md";
    private const string RequirementsContractFile = "requirements-contract.md";
    private const string OrchestrationContractFile = "orchestration-contract.md";

    /// <summary>
    /// The launcher's half of the contract in <see cref="Locate"/>. Renaming it here without
    /// renaming it in <c>bin/planforge-launcher.cmd</c> turns the assertion in
    /// <c>build/package.ps1</c> red, which is the only thing tying the two spellings together.
    /// </summary>
    internal const string RootVariable = "PLANFORGE_PROMPTS";

    private readonly string _root = root ?? Locate(Environment.GetEnvironmentVariable(RootVariable));

    /// <summary>
    /// Three layouts ship, and only two of them can be found by walking up from the binary: publish
    /// output puts prompts beside the executable, and the installed plugin puts the executable under
    /// bin/&lt;rid&gt;/ with the prompts at the plugin root. The third has no prompts above it at all
    /// — the launcher downloads the bare executable into a per-version cache under %LOCALAPPDATA% —
    /// so the launcher, which knows the plugin root, names it through <see cref="RootVariable"/>.
    /// </summary>
    /// <param name="configured">
    /// The value of <see cref="RootVariable"/>. Taken as given rather than probed: the launcher sets
    /// it only when the folder is there, so a value that leads nowhere means a broken install, and
    /// falling back to the walk-up would answer that with the same guess that failed to begin with —
    /// a <see cref="PromptNotFoundException"/> naming a path nobody configured.
    /// </param>
    internal static string Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, PromptsFolder);
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, PromptsFolder);
    }

    public string Load(string vendorId, VendorRole role)
    {
        var path = Path.Combine(_root, vendorId, $"{role.ToString().ToLowerInvariant()}.md");
        if (!File.Exists(path)) 
            throw new PromptNotFoundException(path);

        // A host running a worker for us also hands it whatever the user installed there, and this
        // plugin is usually among that: a cursor builder measured on 2026-08-29 was given the run's
        // own forge skill and started its MCP server. Neither role has any business driving the run
        // it was called into, so both are told to leave that surface alone.
        var prompt = Append(File.ReadAllText(path), OrchestrationContractFile);

        // The shared contracts live once and are appended rather than copied into each vendor's
        // file, which is how the 1.x copies drifted apart.
        return role is VendorRole.Critic ? Append(prompt, RoslynContractFile) : prompt;
    }

    /// <summary>
    /// The critic prompt plus the plan-review requirements contract, which tells the critic that
    /// the plan's own requirements and gates are under review beside its tasks. Kept out of
    /// <see cref="Load"/> for the same reason as the scope contract: the same critic role also
    /// reviews diffs, where no requirements section is in front of it.
    /// </summary>
    public string LoadPlanReviewCritic(string vendorId) =>
        Append(Load(vendorId, VendorRole.Critic), RequirementsContractFile);

    /// <summary>
    /// The critic prompt plus the code-review scope contract. The contract is not folded into
    /// <see cref="Load"/> because the same critic role also reviews plans, where "judge against
    /// the plan" has nothing to attach to.
    /// </summary>
    public string LoadCodeReviewCritic(string vendorId) =>
        Append(Load(vendorId, VendorRole.Critic), ScopeContractFile);

    private string Append(string prompt, string contractFile)
    {
        var contract = Path.Combine(_root, contractFile);
        return File.Exists(contract) ? $"{prompt}\n\n{File.ReadAllText(contract)}" : prompt;
    }
}

internal sealed class PromptNotFoundException(string path) : Exception($"prompt file {path} was not found");
