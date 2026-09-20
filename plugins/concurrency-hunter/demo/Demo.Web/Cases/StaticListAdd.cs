using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.StaticListAdd;

/// <summary>One action appends to a static <see cref="List{T}"/>. Two requests of that action run at once, so the pair is the
/// action against itself: both grow the same list, which is neither atomic on its structure nor on the cell they land in.</summary>
public static class AuditTrail
{
    public static readonly List<string> Log = [];
}

[ApiController]
[Route("cases/static-list-add")]
public sealed class AuditController : ControllerBase
{
    [HttpPost]
    public void Post(string entry) => AuditTrail.Log.Add(entry);
}
