using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The 18-member whole-entry effects oracle.</summary>
[Collection(ModelEvalTests.COLLECTION)]
public sealed class EffectsEvalTests
{
    [Fact]
    public void Recording_cannot_lower_the_exact_effect_entry_floor() => ModelEvalTests.AssertRecordingPreservesFloor("effectsExact");

    [RequiresModelEvalsFact]
    public void No_effect_entry_is_an_unsafe_narrowing()
    {
        var compared = WholeEntryEvals.Compared(EffectEvals.InstalledRun);
        var unsafeEntries = compared.Where(item => item.Comparison.IsUnsafeNarrowing)
                                    .Select(item => $"{item.Answer.Gold.Member}: {item.Comparison.Detail}")
                                    .ToArray();

        Assert.Equal(EffectEvals.MEMBERS, compared.Count);
        Assert.True(unsafeEntries.Length == 0, $"{unsafeEntries.Length} unsafe narrowing(s):\n" + string.Join("\n", unsafeEntries));
    }

    [RequiresModelEvalsFact]
    public void Exact_effect_entries_meet_the_recorded_count()
    {
        var compared = WholeEntryEvals.Compared(EffectEvals.InstalledRun);
        var exact = compared.Count(item => item.Comparison.IsExact);
        var recorded = ModelEvalTests.RecordedCount("effectsExact");

        Assert.True(exact >= recorded,
                    $"{exact} exact effect entries, under the recorded count {recorded}; inexact:\n" +
                    string.Join("\n", compared.Where(item => !item.Comparison.IsExact)
                                               .Select(item => $"{item.Answer.Gold.Member}: {item.Comparison.Detail}")));
    }

    [RequiresModelEvalsFact]
    public void Core_library_members_answer_corelib()
    {
        var answers = EffectEvals.InstalledRun.Answers.Where(answer => answer.Gold.Input.Assembly == "System.Private.CoreLib").ToArray();

        Assert.Equal(EffectEvals.CORELIB_MEMBERS, answers.Length);
        Assert.All(answers, answer =>
        {
            Assert.Equal(GenerationReasons.CORELIB, answer.Answer.Reason);
            Assert.Null(answer.Answer.Model);
        });
    }
}
