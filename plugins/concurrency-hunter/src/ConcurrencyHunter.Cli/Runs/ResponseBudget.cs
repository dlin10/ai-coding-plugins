using System.Text;
using System.Text.Json;
using Common.Mcp;

namespace ConcurrencyHunter.Runs;

internal static class ResponseBudget
{
    internal static string Fit(string text, int maxBytes) => ResponseText.Fitted(text, value => Weight(value!), maxBytes, "…")!;

    internal static int Weight(string text) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(text));
}
