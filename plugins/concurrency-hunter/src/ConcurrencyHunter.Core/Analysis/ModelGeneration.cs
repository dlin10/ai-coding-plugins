namespace ConcurrencyHunter.Analysis;

/// <summary>What an analysis does with the generated model layer (R9): a parameter of the analyzer, never configuration, so that no file or
/// environment variable sets it.</summary>
public enum ModelGeneration
{
    /// <summary>The generated files are read and applied, and a member that no layer describes and no file answers for is generated within
    /// the run's budget and written.</summary>
    Generate,

    /// <summary>The generated files are read and applied; nothing is generated or written, and the result lists the generation targets
    /// a <see cref="Generate"/> run would have generated.</summary>
    ReadOnly,

    /// <summary>The generated layer is off: nothing is read, generated or written.</summary>
    None
}

/// <summary>Which run limit a <see cref="ModelGeneration.Generate"/> run spends its generations within: a parameter of the analyzer, like
/// <see cref="ModelGeneration"/>, so that only a caller that asks for the recording's limit gets it.</summary>
public enum GenerationBudget
{
    /// <summary>The run's limit, the one every run has unless it records.</summary>
    Run,

    /// <summary>The recording's limit: the run that writes a repository's committed generated models.</summary>
    Recording,

    /// <summary>The census budget: the run that regenerates the shared frameworks' census, or settles it, for the owner. It has a run limit
    /// and a member limit of its own (<see cref="RunGeneration.CENSUS_LIMIT"/>, <see cref="RunGeneration.CENSUS_MEMBER_LIMIT"/>) and the
    /// recording's workers.</summary>
    Census
}
