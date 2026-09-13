using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.LambdaAndLocalFunction;

public sealed class Profile
{
    public string? Name { get; set; }
    public string? Email { get; set; }
}

/// <summary>One write in a local function, one in a lambda; both are attributed to the action that holds them.</summary>
[ApiController]
[Route("cases/lambda-and-local-function")]
public sealed class ProfileController : ControllerBase
{
    private readonly Profile _profile;

    public ProfileController(Profile profile) => _profile = profile;

    [HttpPost]
    public void Update(string name, string email)
    {
        SetName();
        Action setEmail = () => _profile.Email = email;
        setEmail();

        void SetName() => _profile.Name = name;
    }
}

public static class LambdaAndLocalFunctionCase
{
    public static IServiceCollection AddLambdaAndLocalFunction(this IServiceCollection services) =>
        services.AddSingleton<Profile>();
}
