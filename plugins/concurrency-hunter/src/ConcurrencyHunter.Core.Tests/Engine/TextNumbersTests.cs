using ConcurrencyHunter.Analysis;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The emission's keys are made of numbers in place of strings, so the numbers must tell apart exactly what the strings do
/// (issue 464).</summary>
public sealed class TextNumbersTests
{
    [Fact]
    public void Equal_strings_get_one_number_and_different_strings_different_numbers()
    {
        var numbers = new TextNumbers();
        var first = numbers.Of("Scope|Assembly|r1|State.Value:Field");
        // An equal value built anew, not the same string object.
        var again = numbers.Of(string.Concat("Scope|Assembly|r1|", "State.Value:Field"));
        var other = numbers.Of("Scope|Assembly|r1|State.value:Field");
        var empty = numbers.Of(string.Empty);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.NotEqual(first, empty);
        Assert.NotEqual(other, empty);
        Assert.Equal(empty, numbers.Of(string.Empty));
    }

    [Fact]
    public void Threads_asking_at_once_get_one_number_per_string_and_different_numbers_for_different_strings()
    {
        // Collectors on several workers share one table (ADR 0040): every thread asks for the same strings at once.
        var numbers = new TextNumbers();
        var texts = Enumerable.Range(0, 2000).Select(index => $"Scope|Assembly|r{index}|State.Value:Field").ToArray();
        var answers = new int[8][];
        Parallel.For(0, answers.Length, thread =>
            answers[thread] = texts.Select(text => numbers.Of(string.Concat(text.AsSpan(0, 6), text.AsSpan(6)))).ToArray());

        foreach (var answer in answers)
            Assert.Equal(answers[0], answer);
        Assert.Equal(texts.Length, answers[0].Distinct().Count());
    }
}
