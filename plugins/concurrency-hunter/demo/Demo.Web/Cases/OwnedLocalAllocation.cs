using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.OwnedLocalAllocation;

public sealed class Scratch
{
    public string? Text { get; set; }
}

/// <summary>Hard negative: every request allocates, writes and reads its own object, and it never escapes.</summary>
[ApiController]
[Route("cases/owned-local-allocation")]
public sealed class ScratchController : ControllerBase
{
    [HttpPost]
    public string? Post(string text)
    {
        var scratch = new Scratch();
        scratch.Text = text;
        return scratch.Text;
    }
}
