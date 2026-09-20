using System.Globalization;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Analysis;

/// <summary>
/// Which cell of an array or a slice an access touches (TD-043): a proven constant, a conservative range, the canonical identity
/// of a value the analysis cannot evaluate, or nothing known at all. An expression the analysis does not support widens the
/// selector to a safe range or to <see cref="Unknown"/>; it is never dropped, because a dropped selector would claim that two
/// cells are the same one. Two selectors prove two different cells only where their values provably cannot meet; the objects in
/// two different cells may still alias.
/// </summary>
public abstract record ElementSelector
{
    /// <summary>Nothing is known about the cell, so it may be any of them.</summary>
    public static ElementSelector Unknown { get; } = new UnknownSelector();

    public static ElementSelector Exact(long index) => new ExactSelector(index);

    /// <summary>The cells from <paramref name="start"/> up to but not including <paramref name="end"/>; an empty or reversed range
    /// is nothing known.</summary>
    public static ElementSelector Range(long start, long end) => end > start ? new RangeSelector(start, end) : Unknown;

    /// <summary>A value the analysis carries by its canonical identity rather than by its text: two accesses under one identity
    /// are still two values of two executions, so it proves nothing on its own until a solver reads it.</summary>
    public static ElementSelector Symbol(string identity) => new SymbolSelector(identity);

    /// <summary>The cell a constant key names. Two keys are one cell when the collection's own comparer says so, which is why a
    /// key is compared only after <see cref="UnderEquality"/> has read that comparer (ADR 0010).</summary>
    public static ElementSelector Key(string literal) => new KeySelector(literal);

    /// <summary>The selector as the collection's comparer leaves it: a comparer that ignores case makes two keys that differ only
    /// in case one cell, and a comparer the analysis cannot read proves nothing about any two keys.</summary>
    public ElementSelector UnderEquality(IrKeyEquality equality) => (this, equality) switch
    {
        (_, IrKeyEquality.Unknown) => Unknown,
        (KeySelector key, IrKeyEquality.IgnoreCase) => Key(key.Literal.ToLowerInvariant()),
        _ => this
    };

    /// <summary>How the path prints the selector: <c>[0]</c>, <c>["a"]</c>, <c>[0..8]</c> and <c>[?]</c> for everything else.</summary>
    public abstract string Text { get; }

    /// <summary>Whether the selector says which cells it names. A pair of two cells is reported on the proven one, since it is
    /// what a reader can act on.</summary>
    public bool IsProven => this is ExactSelector or RangeSelector or KeySelector;

    /// <summary>Whether the two selectors may name one cell. Only proven values decide it; anything else may meet anything.</summary>
    public bool MayOverlap(ElementSelector other) => (this, other) switch
    {
        (ExactSelector first, ExactSelector second) => first.Index == second.Index,
        (ExactSelector exact, RangeSelector range) => range.Contains(exact.Index),
        (RangeSelector range, ExactSelector exact) => range.Contains(exact.Index),
        (RangeSelector first, RangeSelector second) => first.Start < second.End && second.Start < first.End,
        (KeySelector first, KeySelector second) => string.Equals(first.Literal, second.Literal, StringComparison.Ordinal),
        _ => true
    };

    /// <summary>Whether the two selectors name one cell and both prove which: what decides where a check-then-act sequence is
    /// reported, since a dependency between two cells nothing proves the same crosses the whole collection (ADR 0010).</summary>
    public bool IsProvenSameAs(ElementSelector other) => (this, other) switch
    {
        (ExactSelector first, ExactSelector second) => first.Index == second.Index,
        (KeySelector first, KeySelector second) => string.Equals(first.Literal, second.Literal, StringComparison.Ordinal),
        // One value of one execution is one cell, whatever it holds: that two selectors carry its identity is the proof.
        (SymbolSelector first, SymbolSelector second) => string.Equals(first.Identity, second.Identity, StringComparison.Ordinal),
        _ => false
    };

    /// <summary>The selector widened to cover both, for a value that may be either.</summary>
    public ElementSelector Widen(ElementSelector other) => (this, other) switch
    {
        (ExactSelector first, ExactSelector second) when first.Index == second.Index => this,
        (ExactSelector first, ExactSelector second) => Range(Math.Min(first.Index, second.Index), Math.Max(first.Index, second.Index) + 1),
        (RangeSelector first, ExactSelector second) => Range(Math.Min(first.Start, second.Index), Math.Max(first.End, second.Index + 1)),
        (ExactSelector first, RangeSelector second) => Range(Math.Min(second.Start, first.Index), Math.Max(second.End, first.Index + 1)),
        (RangeSelector first, RangeSelector second) => Range(Math.Min(first.Start, second.Start), Math.Max(first.End, second.End)),
        _ => Unknown
    };

    /// <summary>The selector of a cell of a slice that starts at <paramref name="offset"/> of the underlying region: a slice is
    /// compared by that region and this offset, never by the slice object.</summary>
    public ElementSelector Shift(long offset) => this switch
    {
        ExactSelector exact => Exact(exact.Index + offset),
        RangeSelector range => Range(range.Start + offset, range.End + offset),
        _ => Unknown
    };

    private sealed record ExactSelector(long Index) : ElementSelector
    {
        public override string Text => $"[{Index.ToString(CultureInfo.InvariantCulture)}]";
    }

    private sealed record RangeSelector(long Start, long End) : ElementSelector
    {
        internal bool Contains(long index) => index >= Start && index < End;

        public override string Text =>
            $"[{Start.ToString(CultureInfo.InvariantCulture)}..{End.ToString(CultureInfo.InvariantCulture)}]";
    }

    private sealed record KeySelector(string Literal) : ElementSelector
    {
        public override string Text => $"[\"{Literal}\"]";
    }

    private sealed record SymbolSelector(string Identity) : ElementSelector
    {
        public override string Text => "[?]";
    }

    private sealed record UnknownSelector : ElementSelector
    {
        public override string Text => "[?]";
    }
}
