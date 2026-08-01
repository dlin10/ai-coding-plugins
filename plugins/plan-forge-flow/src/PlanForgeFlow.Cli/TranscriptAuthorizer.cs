namespace PlanForgeFlow;

internal static class TranscriptAuthorizer
{
    public static NativeAuthorization AuthorizeCurrent(string transcriptPath, string turnId, string? sessionId)
    {
        var transcript = TranscriptReader.ReadDocument(transcriptPath);
        var current = transcript.Records.Where(record => record.Kind == "message" && record.Role == "user" && record.TurnId == turnId && !TranscriptReader.IsSyntheticEnvironmentMessage(record)).ToArray();
        var prompt = current.Length == 1 ? current[0].Text : null;
        if (prompt is null) throw new CliFailure("state", "the implementation turn has no unique user submission", 3);
        return AuthorizeSubmission(transcriptPath, turnId, sessionId, prompt);
    }

    public static NativeAuthorization AuthorizeSubmission(string transcriptPath, string turnId, string? sessionId, string prompt)
    {
        var transcript = TranscriptReader.ReadDocument(transcriptPath);
        if (string.IsNullOrWhiteSpace(sessionId) || !string.Equals(sessionId, transcript.SessionId, StringComparison.Ordinal))
        {
            throw new CliFailure("state", "current session does not match the transcript", 3);
        }

        var contextMatches = transcript.Records.Where(record => record.Kind == "turn" && record.TurnId == turnId).ToArray();
        if (contextMatches.Length != 1 || contextMatches[0].Mode != "default")
        {
            throw new CliFailure("state", "implementation turn is not a unique Default-mode turn", 3);
        }

        var context = contextMatches[0];
        var nextTurn = transcript.Records.FirstOrDefault(record => record.Kind == "turn" && record.Index > context.Index);
        if (nextTurn is not null) throw new CliFailure("state", "another turn intervened after the implementation submission", 3);
        var previousTurn = transcript.Records.LastOrDefault(record => record.Kind == "turn" && record.Index < context.Index);
        var users = transcript.Records
                              .Where(record => record.Kind == "message" && record.Role == "user" && !TranscriptReader.IsSyntheticEnvironmentMessage(record) &&
                                               ((record.Index > (previousTurn?.Index ?? -1) && record.Index < context.Index) || (record.Index > context.Index && (nextTurn is null || record.Index < nextTurn.Index))))
                              .ToArray();
        if (users.Length != 1 || !string.Equals(users[0].Text, prompt, StringComparison.Ordinal))
        {
            throw new CliFailure("state", "implementation turn must contain exactly one matching user submission", 3);
        }

        var classification = PromptClassifier.Classify(prompt);
        if (classification == "clear-context") return AuthorizeClearContext(transcript, prompt, users[0].Index);
        if (classification is not ("same-context" or "same-context-embedded"))
        {
            throw new CliFailure("state", "prompt is not an authorized native implementation form", 3);
        }

        var candidate = transcript.Records
                                  .Where(record => record.Kind == "message" && record.Role == "assistant" && record.Phase == "final_answer" && record.Mode == "plan" && record.Index < context.Index)
                                  .OrderByDescending(record => record.Index)
                                  .FirstOrDefault(record => record.Text is not null && record.Text.TrimEnd().StartsWith("<proposed_plan>\n", StringComparison.Ordinal) && record.Text.TrimEnd().EndsWith("</proposed_plan>", StringComparison.Ordinal));
        if (candidate?.Text is null) throw new CliFailure("state", "immediately preceding Forge proposed plan is missing", 3);
        EnsureNativeActivitySequence(transcript.Records, candidate.Index, users[0].Index);
        var candidateText = candidate.Text.TrimEnd();
        var body = candidateText["<proposed_plan>\n".Length..^"</proposed_plan>".Length];
        var parsed = ResumeEnvelope.Parse(body);
        var origin = parsed.Envelope["origin"]!.AsObject();
        if (!string.Equals(Path.GetFullPath(origin["transcriptPath"]!.GetValue<string>()), transcript.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !string.Equals(origin["sessionId"]!.GetValue<string>(), transcript.SessionId, StringComparison.Ordinal) ||
            !string.Equals(origin["turnId"]!.GetValue<string>(), candidate.TurnId, StringComparison.Ordinal))
        {
            throw new CliFailure("state", "approved envelope origin does not match the transcript", 3);
        }

        if (classification == "same-context-embedded")
        {
            var embedded = prompt[(PromptClassifier.DesktopEmbeddedPrefix.Length + 1)..];
            var embeddedPlan = embedded.EndsWith("\n", StringComparison.Ordinal) ? embedded : embedded + "\n";
            if (!string.Equals(CanonicalText.NormalizePlan(embeddedPlan), parsed.HumanPlan, StringComparison.Ordinal))
            {
                throw new CliFailure("state", "embedded implementation plan does not match the approved plan", 3);
            }
        }

        return new NativeAuthorization("same-context", parsed.HumanPlan, parsed.Envelope, body);
    }

    private static NativeAuthorization AuthorizeClearContext(TranscriptReader.Transcript current, string prompt, int promptIndex)
    {
        var separator = PromptClassifier.ClearContextPrefix + "\n\n";
        if (!prompt.StartsWith(separator, StringComparison.Ordinal)) throw new CliFailure("state", "clear-context implementation prompt wrapper is malformed", 3);
        var carried = prompt[separator.Length..];
        if (carried.StartsWith("<proposed_plan>\n", StringComparison.Ordinal) && carried.EndsWith("</proposed_plan>", StringComparison.Ordinal)) carried = carried["<proposed_plan>\n".Length..^"</proposed_plan>".Length];
        var parsed = ResumeEnvelope.Parse(carried);
        var origin = parsed.Envelope["origin"]!.AsObject();
        var originPath = Path.GetFullPath(origin["transcriptPath"]!.GetValue<string>());
        if (string.Equals(originPath, current.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new CliFailure("state", "clear-context implementation must use a distinct transcript", 3);
        if (!File.Exists(originPath)) throw new CliFailure("state", "origin transcript is missing", 3);
        var originTranscript = TranscriptReader.ReadDocument(originPath);
        if (!string.Equals(origin["sessionId"]!.GetValue<string>(), originTranscript.SessionId, StringComparison.Ordinal)) throw new CliFailure("state", "origin session does not match the transcript", 3);
        var matches = originTranscript.Records.Where(record => record.Kind == "message" && record.Role == "assistant" && record.Phase == "final_answer" && record.TurnId == origin["turnId"]?.GetValue<string>() && record.Text is not null && record.Text.TrimEnd().StartsWith("<proposed_plan>\n", StringComparison.Ordinal) && record.Text.TrimEnd().EndsWith("</proposed_plan>", StringComparison.Ordinal)).Where(record =>
        {
            try
            {
                var text = record.Text!.TrimEnd();
                var body = text["<proposed_plan>\n".Length..^"</proposed_plan>".Length];
                return string.Equals(ResumeEnvelope.Parse(body).Envelope["origin"]!["itemId"]?.GetValue<string>(), origin["itemId"]?.GetValue<string>(), StringComparison.Ordinal) && string.Equals(body, carried, StringComparison.Ordinal);
            }
            catch { return false; }
        }).ToArray();
        if (matches.Length != 1) throw new CliFailure("state", "origin proposed plan is missing or ambiguous", 3);
        var candidate = matches[0];
        if (originTranscript.Records.Any(record => record.Index > candidate.Index && record.Kind is "message" or "unsupported-activity" && !(record.Kind == "message" && record.Role == "assistant" && record.Phase == "commentary"))) throw new CliFailure("state", "origin proposed plan is no longer terminal", 3);
        EnsureNativeActivitySequence(current.Records, promptIndex, int.MaxValue);
        return new NativeAuthorization("clear-context", parsed.HumanPlan, parsed.Envelope, carried);
    }

    private static void EnsureNativeActivitySequence(IReadOnlyList<TranscriptReader.Record> records, int candidateIndex, int promptIndex)
    {
        foreach (var record in records.Where(record => record.Index != promptIndex && (record.Index > candidateIndex && record.Index < promptIndex || record.Index > promptIndex)))
        {
            if (record.Kind == "turn" || IsAllowedNativeActivity(record)) continue;
            throw new CliFailure("state", "unsupported activity intervened in the native implementation submission", 3);
        }
    }

    private static bool IsAllowedNativeActivity(TranscriptReader.Record record)
        => record.Kind is "other" or "tool" or "reasoning" || TranscriptReader.IsSyntheticEnvironmentMessage(record) ||
           (record.Kind == "message" && record.Role == "assistant" && record.Phase == "commentary");
}