using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Generation.ModelEvals;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>G-7's comparator: which classified fates are an unsafe narrowing of the gold one, and how a member or a parameter without a
/// classification counts. In the normal suite: it reads no installed framework or package.</summary>
public sealed class UnsafeNarrowingTests
{
    private static readonly ClassifiedFate InvokeNow = new(FateClassifier.INVOKE_NOW, null);
    private static readonly ClassifiedFate Iterator = new(FateClassifier.ITERATOR, null);
    private static readonly ClassifiedFate HolderThis = new(FateClassifier.HOLDER, FateClassifier.THIS);

    [Fact]
    public void The_gold_fate_itself_is_safe_and_exact()
    {
        foreach (var gold in new[] { InvokeNow, Iterator, HolderResult, HolderThis, Unknown })
        {
            Assert.True(IsSafe(gold, gold), Show(gold));
            Assert.True(IsExact(gold, gold), Show(gold));
        }
    }

    [Fact]
    public void Unknown_execution_is_safe_against_every_gold_fate()
    {
        foreach (var gold in new[] { InvokeNow, Iterator, HolderResult, HolderThis, GoldFate("di-registration", null) })
        {
            Assert.True(IsSafe(Unknown, gold), Show(gold));
            Assert.False(IsExact(Unknown, gold), Show(gold));
        }
    }

    [Fact]
    public void Holder_result_where_the_gold_is_iterator_is_safe_but_not_exact()
    {
        Assert.True(IsSafe(HolderResult, Iterator));
        Assert.False(IsExact(HolderResult, Iterator));
    }

    [Fact]
    public void Holder_this_where_the_gold_is_iterator_is_unsafe() => Assert.False(IsSafe(HolderThis, Iterator));

    [Fact]
    public void Invoke_now_where_the_gold_is_iterator_or_holder_is_unsafe()
    {
        Assert.False(IsSafe(InvokeNow, Iterator));
        Assert.False(IsSafe(InvokeNow, HolderResult));
        Assert.False(IsSafe(InvokeNow, HolderThis));
    }

    [Fact]
    public void Iterator_where_the_gold_is_invoke_now_or_holder_is_unsafe()
    {
        Assert.False(IsSafe(Iterator, InvokeNow));
        Assert.False(IsSafe(Iterator, HolderResult));
    }

    [Fact]
    public void Holder_where_the_gold_is_invoke_now_is_unsafe()
    {
        Assert.False(IsSafe(HolderResult, InvokeNow));
        Assert.False(IsSafe(HolderThis, InvokeNow));
    }

    [Fact]
    public void A_holder_of_the_wrong_kind_is_unsafe()
    {
        Assert.False(IsSafe(HolderThis, HolderResult));
        Assert.False(IsSafe(HolderResult, HolderThis));
        Assert.False(IsSafe(new ClassifiedFate(FateClassifier.HOLDER, null), HolderResult));
    }

    [Fact]
    public void Any_fate_but_unknown_execution_where_the_gold_is_a_framework_event_is_unsafe()
    {
        var gold = GoldFate("framework-event", null);

        Assert.Equal(Unknown, gold);
        Assert.True(IsSafe(Unknown, gold));
        foreach (var answered in new[] { InvokeNow, Iterator, HolderResult, HolderThis })
            Assert.False(IsSafe(answered, gold), Show(answered));
    }

    [Fact]
    public void Gold_labels_map_to_the_vocabulary()
    {
        Assert.Equal(Unknown, GoldFate("framework-event", null));
        Assert.Equal(new ClassifiedFate("di-factory", null), GoldFate("di-registration", null));
        Assert.Equal(InvokeNow, GoldFate("invoke-now", null));
        Assert.Equal(Iterator, GoldFate("iterator", null));
        Assert.Equal(HolderResult, GoldFate("holder", "result"));
    }

    [Fact]
    public void A_member_with_no_classification_counts_as_unknown_execution_for_each_gold_parameter()
    {
        var answer = Answer(null, GenerationReasons.CLOSURE_BOUND);

        foreach (var parameter in new[] { "sleepDurationProvider", "onRetry" })
        {
            Assert.Equal(Unknown, Answered(answer, parameter));
            Assert.True(IsSafe(Answered(answer, parameter), HolderResult));
            Assert.False(IsExact(Answered(answer, parameter), HolderResult));
        }
    }

    [Fact]
    public void A_gold_parameter_the_classification_leaves_out_counts_as_unknown_execution()
    {
        var answer = Answer(new Dictionary<string, ClassifiedFate> { ["keySelector"] = Iterator }, null);

        Assert.Equal(Iterator, Answered(answer, "keySelector"));
        Assert.Equal(Unknown, Answered(answer, "resultSelector"));
    }

    [Fact]
    public void The_carried_parser_is_compared_as_its_own_parameter()
    {
        var gold = GoldFate("holder", "result");

        Assert.True(IsExact(Answered(Answer(new Dictionary<string, ClassifiedFate> { ["parser"] = HolderResult }, null), "parser"), gold));
        Assert.False(IsSafe(Answered(Answer(new Dictionary<string, ClassifiedFate> { ["parser"] = HolderThis }, null), "parser"), gold));
        Assert.False(IsSafe(Answered(Answer(new Dictionary<string, ClassifiedFate> { ["parser"] = InvokeNow }, null), "parser"), gold));
        Assert.True(IsSafe(Answered(Answer(new Dictionary<string, ClassifiedFate> { ["parser"] = Unknown }, null), "parser"), gold));
    }

    [Fact]
    public void The_gold_file_gives_the_evals_18_members_24_parameters_and_five_holder_kinds()
    {
        var gold = ReadGold(GoldPath);

        Assert.Equal(GOLD_MEMBERS, gold.Count);
        Assert.Equal(GOLD_PARAMETERS, gold.Sum(member => member.DelegateParams.Count));
        Assert.Equal(["ForMessage", "MessageParserCtor", "WaitAndRetry", "WaitAndRetryAsync", "WaitAndRetryForeverAsync"],
                     gold.Where(member => member.Holder is not null).Select(member => member.Key).Order(StringComparer.Ordinal));
        Assert.All(gold.Where(member => member.Label == FateClassifier.HOLDER), member => Assert.Equal(HolderResult, member.Fate));
    }

    private static GeneratedAnswer Answer(IReadOnlyDictionary<string, ClassifiedFate>? classified, string? reason) =>
        new(1, "M:Lib.C.M(System.Action)", new GenerationAssembly("Lib", "1.0", null), classified, reason,
            new GenerationRecord(null, null, [], 0, [], new Dictionary<string, string>(), [], 0, 0));
}
