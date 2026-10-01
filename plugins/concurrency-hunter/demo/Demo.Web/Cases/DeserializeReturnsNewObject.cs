using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.DeserializeReturnsNewObject;

public sealed class Address
{
    public int Changes;
}

public sealed class Profile
{
    public Address Home { get; set; } = null!;
}

public sealed class ProfileStore
{
    public Profile? Current;
}

public sealed class ProfileWorker : BackgroundService
{
    private readonly ProfileStore _store;

    public ProfileWorker(ProfileStore store) => _store = store;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.Current = System.Text.Json.JsonSerializer.Deserialize<Profile>("{\"Home\":{}}");
        return Task.CompletedTask;
    }
}

[ApiController]
[Route("cases/deserialize-returns-new-object")]
public sealed class ProfileController : ControllerBase
{
    private readonly ProfileStore _store;

    public ProfileController(ProfileStore store) => _store = store;

    [HttpPost]
    public void Post() { var current = _store.Current; if (current is not null) current.Home.Changes++; }
}

public static class DeserializeReturnsNewObjectCase
{
    public static IServiceCollection AddDeserializeReturnsNewObject(this IServiceCollection services) =>
        services.AddSingleton<ProfileStore>().AddHostedService<ProfileWorker>();
}
