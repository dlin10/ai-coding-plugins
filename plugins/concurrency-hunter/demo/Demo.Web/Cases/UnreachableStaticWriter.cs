using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.UnreachableStaticWriter;

/// <summary>Nothing calls <see cref="Import"/>: its body is outside the reachable set. The name deliberately
/// avoids the <c>Controller</c> suffix, which ASP.NET Core would discover as a controller.</summary>
public sealed class LegacyImporter
{
    internal static string? LastFile;

    public void Import(string file) => LastFile = file;
}

/// <summary>Hard negative: a reachable read against a write no root can reach.</summary>
[ApiController]
[Route("cases/unreachable-static-writer")]
public sealed class ImportStatusController : ControllerBase
{
    [HttpGet]
    public string? Get() => LegacyImporter.LastFile;
}
