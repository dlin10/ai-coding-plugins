using Demo.Domain.Cases.SharedLibraryStatic;
using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.SharedLibraryStatic;

/// <summary>A web action can overlap itself when it writes static state from a shared library.</summary>
[ApiController]
[Route("cases/shared-library-static")]
public sealed class SyncController : ControllerBase
{
    [HttpPost]
    public void Post(string source) => LastSync.Source = source;
}
