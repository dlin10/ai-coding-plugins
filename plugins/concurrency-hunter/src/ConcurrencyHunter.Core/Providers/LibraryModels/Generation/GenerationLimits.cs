namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The server's limits shared by run generation, generate and model evals.</summary>
public static class GenerationLimits
{
    /// <summary>One member, including both initializer attempts.</summary>
    public static readonly TimeSpan MEMBER = TimeSpan.FromSeconds(120);
}
