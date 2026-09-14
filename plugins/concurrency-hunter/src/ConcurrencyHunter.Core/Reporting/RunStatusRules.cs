namespace ConcurrencyHunter.Reporting;

public static class RunStatusRules
{
    public static StatusDecision Decide(StatusInputs inputs)
    {
        if (inputs.LoadFailed)
            return new StatusDecision(RunStatus.Failed, ["LoadFailed"]);
        if (inputs.AnalysisFailed)
            return new StatusDecision(RunStatus.Failed, ["AnalysisFailed"]);

        var reasons = new List<string>();
        if (!inputs.LoadComplete)
            reasons.Add("ProjectsNotLoaded");
        if (inputs.DeadlineExceeded)
            reasons.Add("OverallTimeout");
        if (inputs.Analysis is null)
        {
            reasons.Add("AnalysisNotFinished");
        }
        else
        {
            foreach (var group in inputs.Analysis.Groups)
            {
                if ((group.ConfidenceLabel == "High" || group.ConfidenceLabel == "Medium") &&
                    !inputs.NarratedGroupIds.Contains(group.GroupId))
                {
                    reasons.Add($"NarrativeMissing:{group.GroupId}");
                }
            }
        }

        if (reasons.Count > 0)
            return new StatusDecision(RunStatus.Incomplete, reasons);

        return new StatusDecision(inputs.Analysis!.Findings.Count > 0 ? RunStatus.CompleteWithFindings : RunStatus.CompleteClean, []);
    }
}
