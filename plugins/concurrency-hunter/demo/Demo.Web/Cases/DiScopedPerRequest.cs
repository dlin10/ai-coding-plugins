using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.DiScopedPerRequest;

public sealed class RequestNotes
{
    public string? Note { get; set; }
}

/// <summary>Hard negative: the same write/read shape as a singleton race, but each request gets its own
/// scoped instance.</summary>
[ApiController]
[Route("cases/di-scoped-per-request")]
public sealed class NotesController : ControllerBase
{
    private readonly RequestNotes _notes;

    public NotesController(RequestNotes notes) => _notes = notes;

    [HttpPost]
    public void Post(string note) => _notes.Note = note;

    [HttpGet]
    public string? Get() => _notes.Note;
}

public static class DiScopedPerRequestCase
{
    public static IServiceCollection AddDiScopedPerRequest(this IServiceCollection services) =>
        services.AddScoped<RequestNotes>();
}
