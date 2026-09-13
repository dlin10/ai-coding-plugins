using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.DiTransient;

public sealed class DraftBuffer
{
    public string? Text { get; set; }
}

/// <summary>Hard negative: a transient is a new instance on every resolution.</summary>
[ApiController]
[Route("cases/di-transient")]
public sealed class DraftBufferController : ControllerBase
{
    private readonly DraftBuffer _buffer;

    public DraftBufferController(DraftBuffer buffer) => _buffer = buffer;

    [HttpPost]
    public void Post(string text) => _buffer.Text = text;

    [HttpGet]
    public string? Get() => _buffer.Text;
}

public static class DiTransientCase
{
    public static IServiceCollection AddDiTransient(this IServiceCollection services) =>
        services.AddTransient<DraftBuffer>();
}
