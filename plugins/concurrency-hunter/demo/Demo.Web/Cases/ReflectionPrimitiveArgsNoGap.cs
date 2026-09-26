using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.ReflectionPrimitiveArgsNoGap;

public static class Formatter
{
    public static string Format(string name, int count) => $"{name}:{count}";
}

/// <summary>A singleton only read: a reflection call handed nothing but a string and a number touches no region that is not owned,
/// so it is no semantic gap (TD-034).</summary>
public sealed class Greeting
{
    public string Text = "hello";
}

[ApiController]
[Route("cases/reflection-primitive-args-no-gap")]
public sealed class GreetingController : ControllerBase
{
    private readonly Greeting _greeting;

    public GreetingController(Greeting greeting) => _greeting = greeting;

    [HttpGet]
    public object? Get() => typeof(Formatter).GetMethod(nameof(Formatter.Format))!.Invoke(null, new object[] { _greeting.Text, 1 });
}

public static class ReflectionPrimitiveArgsNoGapCase
{
    public static IServiceCollection AddReflectionPrimitiveArgsNoGap(this IServiceCollection services) =>
        services.AddSingleton<Greeting>();
}
