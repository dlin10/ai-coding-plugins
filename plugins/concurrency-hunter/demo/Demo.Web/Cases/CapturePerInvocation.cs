using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.CapturePerInvocation;

/// <summary>Hard negative: every request captures its own <c>sum</c>, and <c>List.ForEach</c> runs the lambda in
/// the request's own execution. Two requests never share the cell.</summary>
[ApiController]
[Route("cases/capture-per-invocation")]
public sealed class SumController : ControllerBase
{
    [HttpPost]
    public int Post(List<int> items)
    {
        var sum = 0;
        items.ForEach(i => sum += i);
        return sum;
    }
}
