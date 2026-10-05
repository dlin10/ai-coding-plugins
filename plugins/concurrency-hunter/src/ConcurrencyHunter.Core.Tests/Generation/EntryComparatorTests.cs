using ConcurrencyHunter.Providers;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>The whole-entry unsafe-narrowing relation of SPEC 12.1 item 8a.</summary>
public sealed class EntryComparatorTests
{
    [Theory]
    [InlineData(LibraryFateKind.InvokeNow)]
    [InlineData(LibraryFateKind.Holder)]
    [InlineData(LibraryFateKind.Startup)]
    [InlineData(LibraryFateKind.Iterator)]
    [InlineData(LibraryFateKind.UnknownExecution)]
    public void A_missing_zero_parameter_fate_is_an_unsafe_narrowing(LibraryFateKind kind)
    {
        var fate = Fate("f", kind, kind == LibraryFateKind.Holder ? LibraryHolderKind.This : null);
        Unsafe(Answer(Model()), EntryTruth.Entry(Model(fates: [fate])));
        Assert.False(EntryComparator.Compare(Answer(Model(fates: [fate])), EntryTruth.Entry(Model(fates: [fate]))).IsUnsafeNarrowing);
        Unsafe(Answer(Model()), EntryTruth.Entry(Model(fates: [fate with { Inputs = [] }])));
    }

    [Fact]
    public void An_entry_against_an_opaque_truth_is_an_unsafe_narrowing() => Unsafe(Answer(Model()), EntryTruth.Opaque);

    [Fact]
    public void An_entry_against_a_model_reason_is_an_unsafe_narrowing() =>
        Unsafe(Answer(Model()), EntryTruth.NoEntry(ModelReasons.VOCABULARY));

    [Fact]
    public void A_missing_effect_is_an_unsafe_narrowing() =>
        Unsafe(Answer(Model()), EntryTruth.Entry(Model(effects: [LibraryEffect.DeepReadOf("p")])));

    [Fact]
    public void A_missing_kept_value_is_an_unsafe_narrowing() =>
        Unsafe(Answer(Model()), EntryTruth.Entry(Model(keeps: Values("this", "arg:p"))));

    [Fact]
    public void A_missing_store_value_is_an_unsafe_narrowing() =>
        Unsafe(Answer(Model()), EntryTruth.Entry(Model(stores: Values("cells", "arg:p"))));

    [Fact]
    public void A_missing_result_is_an_unsafe_narrowing() =>
        Unsafe(Answer(Model()), EntryTruth.Entry(Model(result: Result("[arg:p]"))));

    [Fact]
    public void A_result_of_another_form_is_an_unsafe_narrowing() =>
        Unsafe(Answer(Model(result: Result("collection(arg:p)"))), EntryTruth.Entry(Model(result: Result("[arg:p]"))));

    [Fact]
    public void A_result_missing_a_nested_value_is_an_unsafe_narrowing() =>
        Unsafe(Answer(Model(result: Result("[sequence(arg:p)]"))), EntryTruth.Entry(Model(result: Result("[sequence(arg:p,arg:q)]"))));

    [Fact]
    public void A_missing_output_is_an_unsafe_narrowing() =>
        Unsafe(Answer(Model()), EntryTruth.Entry(Model(outputs: Outputs(("o", "[arg:p]")))));

    [Fact]
    public void An_output_of_another_form_is_an_unsafe_narrowing() =>
        Unsafe(Answer(Model(outputs: Outputs(("o", "collection(arg:p)")))),
               EntryTruth.Entry(Model(outputs: Outputs(("o", "[arg:p]")))));

    [Fact]
    public void An_output_lacking_a_value_is_an_unsafe_narrowing() =>
        Unsafe(Answer(Model(outputs: Outputs(("o", "[arg:p]")))),
               EntryTruth.Entry(Model(outputs: Outputs(("o", "[arg:p,arg:q]")))));

    [Fact]
    public void A_narrower_fate_kind_is_an_unsafe_narrowing() =>
        Unsafe(Answer(Model(fates: [Fate("f", LibraryFateKind.InvokeNow)])),
               EntryTruth.Entry(Model(fates: [Fate("f", LibraryFateKind.Iterator)])));

    [Fact]
    public void A_missing_input_value_is_an_unsafe_narrowing() =>
        Unsafe(Answer(Model(fates: [Fate("f", LibraryFateKind.InvokeNow, inputs: [[]])])),
               EntryTruth.Entry(Model(fates: [Fate("f", LibraryFateKind.InvokeNow, inputs: [[Value("arg:p")]])])));

    [Fact]
    public void Wider_answers_are_safe_but_not_exact()
    {
        var truth = Model(effects: [LibraryEffect.DeepReadOf("p")], result: Result("[arg:p]"),
                          keeps: Values("this", "arg:p"), stores: Values("cells", "arg:p"),
                          outputs: Outputs(("o", "[arg:p]")),
                          fates: [Fate("f", LibraryFateKind.InvokeNow, inputs: [[Value("arg:p")]])]);
        var wider = Model(effects: [LibraryEffect.DeepReadOf("p"), LibraryEffect.WriteOf("p")], result: Result("[arg:p,arg:q]"),
                          keeps: Values("this", "arg:p", "arg:q"), stores: Values("cells", "arg:p", "arg:q"),
                          outputs: Outputs(("o", "[arg:p,arg:q]")),
                          fates: [Fate("f", LibraryFateKind.UnknownExecution, inputs: [[Value("arg:p"), Value("arg:q")]])]);

        var comparison = EntryComparator.Compare(Answer(wider), EntryTruth.Entry(truth));

        Assert.False(comparison.IsUnsafeNarrowing, comparison.Detail);
        Assert.False(comparison.IsExact, comparison.Detail);
    }

    [Fact]
    public void Canonically_equal_answers_are_exact()
    {
        var truth = Model(result: Result("[arg:p,arg:q]"), keeps: Values("this", "arg:p", "arg:q"),
                          fates: [Fate("f", LibraryFateKind.InvokeNow, inputs: [[Value("arg:p"), Value("arg:q")]])]);
        var reordered = Model(result: Result("[arg:q,arg:p]"), keeps: Values("this", "arg:q", "arg:p"),
                              fates: [Fate("f", LibraryFateKind.InvokeNow, inputs: [[Value("arg:q"), Value("arg:p")]])]);

        var comparison = EntryComparator.Compare(Answer(reordered), EntryTruth.Entry(truth));

        Assert.False(comparison.IsUnsafeNarrowing, comparison.Detail);
        Assert.True(comparison.IsExact, comparison.Detail);
    }

    [Fact]
    public void No_entry_answers_are_exact_only_by_reason_when_requested()
    {
        var answer = Answer(null, ModelReasons.UNKNOWN_TOUCH);

        Assert.True(EntryComparator.Compare(answer, EntryTruth.NoEntry(ModelReasons.VOCABULARY)).IsExact);
        Assert.False(EntryComparator.Compare(answer, EntryTruth.NoEntry(ModelReasons.VOCABULARY), requireSameModelReason: true).IsExact);
        Assert.True(EntryComparator.Compare(answer, EntryTruth.NoEntry(ModelReasons.UNKNOWN_TOUCH), requireSameModelReason: true).IsExact);
    }

    private static void Unsafe(GeneratedAnswer answer, EntryTruth truth)
    {
        var comparison = EntryComparator.Compare(answer, truth);
        Assert.True(comparison.IsUnsafeNarrowing, comparison.Detail);
        Assert.False(comparison.IsExact, comparison.Detail);
    }

    private static GeneratedAnswer Answer(LibraryModel? model, string? modelReason = null) =>
        new(2, "M:Lib.Api.M(Lib.Item)", new GenerationAssembly("Lib", "1.0", null), new Dictionary<string, ClassifiedFate>(), null,
            model, modelReason, new GenerationRecord(null, null, [], 0, [], new Dictionary<string, string>(), [], 0, [],
                                                     new Dictionary<string, IReadOnlyList<string>>(), 0, 0));

    private static LibraryModel Model(IReadOnlyList<LibraryEffect>? effects = null, LibraryResult? result = null,
                                      IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>>? keeps = null,
                                      IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>>? stores = null,
                                      IReadOnlyDictionary<string, LibraryResult>? outputs = null,
                                      IReadOnlyList<LibraryFate>? fates = null) =>
        new("M:Lib.Api.M(Lib.Item)", [new SupportedAssemblyVersion("Lib", new Version(1, 0), new Version(2, 0))], effects ?? [])
        {
            Result = result,
            Keeps = keeps ?? new Dictionary<string, IReadOnlyList<LibraryValue>>(),
            Stores = stores ?? new Dictionary<string, IReadOnlyList<LibraryValue>>(),
            Outputs = outputs ?? new Dictionary<string, LibraryResult>(),
            Fates = fates ?? []
        };

    private static LibraryFate Fate(string parameter, LibraryFateKind kind, LibraryHolderKind? holder = null,
                                    IReadOnlyList<IReadOnlyList<LibraryValue>>? inputs = null) =>
        new(parameter, kind, holder, inputs);

    private static IReadOnlyDictionary<string, IReadOnlyList<LibraryValue>> Values(string place, params string[] values) =>
        new Dictionary<string, IReadOnlyList<LibraryValue>>(StringComparer.Ordinal) { [place] = values.Select(Value).ToArray() };

    private static IReadOnlyDictionary<string, LibraryResult> Outputs(params (string Parameter, string Result)[] outputs) =>
        outputs.ToDictionary(output => output.Parameter, output => Result(output.Result), StringComparer.Ordinal);

    private static LibraryValue Value(string text) => LibraryValue.Parse(text)!;

    private static LibraryResult Result(string text) => LibraryResult.Parse(text)!;
}
