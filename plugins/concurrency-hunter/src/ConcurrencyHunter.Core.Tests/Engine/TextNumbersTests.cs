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
}
