using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.InterlockedCompareExchangeLoop;

/// <summary>A compare-and-swap loop: the action reads the total into a local, computes the next value and publishes it
/// with <see cref="Interlocked.CompareExchange(ref int, int, int)"/>, retrying while another execution got there
/// first. The read is stale by design and the swap rejects it, so the loop is not a lost update.</summary>
[ApiController]
[Route("cases/interlocked-compare-exchange-loop")]
public sealed class RunningTotalController : ControllerBase
{
    private static int _total;

    [HttpPost]
    public void Post(int sample)
    {
        int current;
        int updated;
        do
        {
            current = _total;
            updated = current + sample;
        }
        while (Interlocked.CompareExchange(ref _total, updated, current) != current);
    }
}
