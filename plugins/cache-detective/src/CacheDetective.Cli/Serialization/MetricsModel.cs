namespace CacheDetective.Serialization;

/// <summary>One coverage number and whether there was one to give.</summary>
internal sealed record CoverageMetric(double? Value, string Status);

internal sealed record MeasurementCounts(int Vertices, int Edges, int CacheOperations);

internal sealed record MeasurementCoverage(CoverageMetric Cache, CoverageMetric Sql);

/// <summary>
/// One measurement of one corpus. The flat fields and the grouped ones carry the same numbers on purpose:
/// the per-run reports read counts and coverage as groups, while the measurement tests read them flat.
/// Both are written from the same values, so neither view can drift from the other.
/// </summary>
internal sealed record Measurement(string Solution, double LoadSeconds, double IndexSeconds, double TotalSeconds, int Vertices,
                                   int Edges, int CacheOperations, IReadOnlyDictionary<string, int> Unresolved, int Diagnostics,
                                   int ProjectsExpected, int ProjectsLoaded, IReadOnlyList<string> MissingProjects,
                                   IReadOnlyList<string> EmptyProjects, bool LoadComplete, string Revision, bool WorkingTreeClean, string? RecognizersFile,
                                   string? RecognizersHash, int RecognizersApplied, CoverageMetric CacheSiteCoverage,
                                   CoverageMetric SqlSiteCoverage, bool CleanWorktree, MeasurementCounts Counts,
                                   MeasurementCoverage Coverage);

internal sealed record SampleRow(string Id, string File, int Line, string Prediction, string? Verdict = null,
                                 string? Justification = null);

internal sealed record SampleScores(double? Accuracy, double? FalsePositiveRate, int UnclearCount, string Status);

internal sealed record MetricsSample(string Sample, int TargetCount, int CandidateCount, string Revision,
                                     IReadOnlyList<SampleRow> Rows, SampleScores? Metrics = null);
