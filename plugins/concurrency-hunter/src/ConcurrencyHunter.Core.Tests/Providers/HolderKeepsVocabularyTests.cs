using ConcurrencyHunter.Core.Tests.Engine;
using ConcurrencyHunter.Providers.LibraryModels;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class HolderKeepsVocabularyTests
{
    private const string HOLDER = "\"effects\":{},\"keeps\":{\"result\":[\"arg:value\"]},\"fates\":{\"callback\":{\"fate\":\"holder\",\"holder\":\"result\",\"inputs\":[[]]}}";

    [Fact]
    public void Result_keeper_with_a_result_holder_is_accepted()
    {
        var (_, rejections) = ModelVocabularyFixture.Resolve(ModelVocabularyFixture.Entry("Hold", HOLDER));
        Assert.Empty(rejections);
    }

    [Fact]
    public void Result_keeper_without_new_or_a_result_holder_is_refused()
    {
        var decision = "\"effects\":{},\"keeps\":{\"result\":[\"arg:value\"]},\"fates\":{\"callback\":{\"fate\":\"invoke-now\",\"inputs\":[[]]}}";
        var (_, rejections) = ModelVocabularyFixture.Resolve(ModelVocabularyFixture.Entry("Hold", decision));
        Assert.Contains("result new", Assert.Single(rejections).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Result_beside_a_result_holder_is_still_refused()
    {
        var decision = HOLDER + ",\"result\":\"[arg:value]\"";
        var (_, rejections) = ModelVocabularyFixture.Resolve(ModelVocabularyFixture.Entry("Hold", decision));
        Assert.Contains("carries no result", Assert.Single(rejections).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Result_holder_keeps_on_the_object_the_call_returns()
    {
        var run = ModelVocabularyFixture.Run("""
            var value = new NewInputs.Payload();
            var holder = NewInputs.Lib.Hold(value, _ => { });
            holder.Run();
            """, ModelVocabularyFixture.Entry("Hold", HOLDER));

        var holder = Assert.Single(run.Heap.Regions.Values, region => region.TypeKey == "NewInputs:NewInputs.Holder");
        var kept = Assert.Single(run.Heap.PointsTo(holder.Identity, "[kept]"));
        Assert.Equal("NewInputs:NewInputs.Payload", run.Heap.Regions[kept].TypeKey);
    }
}
