using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The built-in 129-entry LINQ family as a whole-entry oracle.</summary>
[Collection(ModelEvalTests.COLLECTION)]
public sealed class LinqOracleTests
{
    [Theory]
    [InlineData("System.Linq")]
    [InlineData("System.Linq.Queryable")]
    public void Recording_cannot_lower_an_exact_linq_entry_floor(string group) => ModelEvalTests.AssertRecordingPreservesFloor("linqExact", group);

    [RequiresModelEvalsFact]
    public void Every_linq_entry_is_generated_in_its_own_assembly()
    {
        var answers = LinqEvals.InstalledRun.Answers;

        Assert.Equal(LinqEvals.MEMBERS, answers.Count);
        Assert.Equal(LinqEvals.ENUMERABLE_MEMBERS, answers.Count(answer => answer.Gold.Group == "System.Linq"));
        Assert.Equal(LinqEvals.QUERYABLE_MEMBERS, answers.Count(answer => answer.Gold.Group == "System.Linq.Queryable"));
        Assert.All(answers, answer =>
        {
            Assert.Equal(answer.Gold.Group, answer.Answer.Assembly.Name);
            Assert.NotEqual(GenerationReasons.NO_IMPLEMENTATION, answer.Answer.Reason);
            Assert.NotEqual(GenerationReasons.MEMBER_NOT_FOUND, answer.Answer.Reason);
        });
    }

    [RequiresModelEvalsFact]
    public void No_generated_linq_entry_is_narrower_than_the_built_in()
    {
        var compared = WholeEntryEvals.Compared(LinqEvals.InstalledRun);
        var unsafeEntries = compared.Where(item => item.Comparison.IsUnsafeNarrowing)
                                    .Select(item => $"{item.Answer.Gold.Member}: {item.Comparison.Detail}")
                                    .ToArray();

        Assert.Equal(LinqEvals.MEMBERS, compared.Count);
        var counts = string.Join("; ", compared.GroupBy(item => item.Answer.Gold.Group, StringComparer.Ordinal)
                                               .Select(group => $"{group.Key}: {group.Count()} entries, " +
                                                                $"{group.Count(item => item.Comparison.IsUnsafeNarrowing)} unsafe narrowings"));
        Assert.True(unsafeEntries.Length == 0, counts + ":\n" + string.Join("\n", unsafeEntries));
    }

    [RequiresModelEvalsFact]
    public void Exact_linq_entries_meet_the_recorded_count()
    {
        var compared = WholeEntryEvals.Compared(LinqEvals.InstalledRun);
        var exact = compared.Where(item => item.Comparison.IsExact)
                            .GroupBy(item => item.Answer.Gold.Group, StringComparer.Ordinal)
                            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var group in new[] { "System.Linq", "System.Linq.Queryable" })
        {
            var count = exact.GetValueOrDefault(group);
            var recorded = ModelEvalTests.RecordedLinqExact(group);
            Assert.True(count >= recorded,
                        $"{group}: {count} exact entries, under the recorded count {recorded}; inexact:\n" +
                        string.Join("\n", compared.Where(item => item.Answer.Gold.Group == group && !item.Comparison.IsExact)
                                                   .Select(item => $"{item.Answer.Gold.Member}: {item.Comparison.Detail}")));
        }
    }
}
