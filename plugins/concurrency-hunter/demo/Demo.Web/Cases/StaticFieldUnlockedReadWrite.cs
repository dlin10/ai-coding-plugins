using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.StaticFieldUnlockedReadWrite;

/// <summary>A static written by one action and read by another, with no protection: the POST races
/// with itself and with the GET.</summary>
[ApiController]
[Route("cases/static-field-unlocked-read-write")]
public sealed class LastVisitorController : ControllerBase
{
    private static string? _lastVisitor;

    [HttpPost]
    public void Post(string name) => _lastVisitor = name;

    [HttpGet]
    public string? Get() => _lastVisitor;
}
