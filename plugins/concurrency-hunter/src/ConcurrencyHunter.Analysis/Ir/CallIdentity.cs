using ConcurrencyHunter.Analysis;

namespace ConcurrencyHunter.Ir;

/// <summary>A call's identity across lowerings, independent of the operations admission adds or removes.</summary>
/// <param name="BodyId">The body containing the call.</param>
/// <param name="Span">The source span from which the call was lowered.</param>
/// <param name="Target">The target member's body id.</param>
/// <param name="Ordinal">The zero-based ordinal among calls with the same span and target, in lowering order.</param>
public sealed record CallIdentity(string BodyId, SourceSpan Span, string Target, int Ordinal)
{
    /// <summary>Indexes every call operation of one lowered body.</summary>
    /// <param name="body">The lowered body.</param>
    public static IReadOnlyDictionary<int, CallIdentity> Of(IrBody body)
    {
        var calls = new Dictionary<int, CallIdentity>();
        var ordinals = new Dictionary<(SourceSpan Span, string Target), int>();
        foreach (var call in body.Blocks.SelectMany(block => block.Operations).OfType<IrCallOperation>().OrderBy(call => call.Id))
        {
            WorkCancellation.Check();
            var key = (call.Provenance.Span, call.TargetMethodId ?? call.Method);
            var ordinal = ordinals.GetValueOrDefault(key);
            ordinals[key] = ordinal + 1;
            calls.Add(call.Id, new CallIdentity(body.BodyId, key.Span, key.Item2, ordinal));
        }
        return calls;
    }
}
