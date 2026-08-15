using PlanForge.Vendors;

namespace PlanForge.Prompts;

/// <summary>
/// Role prompts live as files under <c>prompts/&lt;vendor&gt;/&lt;role&gt;.md</c> so they can be
/// edited and tuned per project without rebuilding the binary.
/// </summary>
internal sealed class PromptLibrary
{
    private const string PromptsFolder = "prompts";
    private const string RoslynContractFile = "roslyn-contract.md";

    private readonly string _root;

    public PromptLibrary(string? root = null) => _root = root ?? Locate();

    /// <summary>
    /// Walks up from the binary. The two shipped layouts differ: publish output puts prompts beside
    /// the executable, while the installed plugin puts the executable under bin/&lt;rid&gt;/ and the
    /// prompts at the plugin root.
    /// </summary>
    private static string Locate()
    {
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
        if (!File.Exists(path)) throw new PromptNotFoundException(path);

        var prompt = File.ReadAllText(path);
        if (role is not VendorRole.Critic) return prompt;

        // The Roslyn contract is shared across vendors, so it lives once and is appended rather
        // than copied into each critic file, which is how the 1.x copies drifted apart.
        var contract = Path.Combine(_root, RoslynContractFile);
        return File.Exists(contract) ? $"{prompt}\n\n{File.ReadAllText(contract)}" : prompt;
    }
}

internal sealed class PromptNotFoundException(string path) : Exception($"prompt file {path} was not found");
