using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.RefParameterChainBeyondDepth;

public sealed class Counter
{
    public int Value;
}

public static class Chain
{
    public static void Step1(ref int value) => Step2(ref value);
    public static void Step2(ref int value) => Step3(ref value);
    public static void Step3(ref int value) => Step4(ref value);
    public static void Step4(ref int value) => Step5(ref value);
    public static void Step5(ref int value) => Step6(ref value);
    public static void Step6(ref int value) => Step7(ref value);
    public static void Step7(ref int value) => Step8(ref value);
    public static void Step8(ref int value) => Step9(ref value);
    public static void Step9(ref int value) => value++;
}

[ApiController]
[Route("cases/ref-parameter-chain-beyond-depth")]
public sealed class CounterController : ControllerBase
{
    private readonly Counter _counter;
    public CounterController(Counter counter) => _counter = counter;

    [HttpPost]
    public void Post() => Chain.Step1(ref _counter.Value);

    [HttpPut]
    public void Put() => Chain.Step1(ref _counter.Value);
}

public static class RefParameterChainBeyondDepthCase
{
    public static IServiceCollection AddRefParameterChainBeyondDepth(this IServiceCollection services) =>
        services.AddSingleton<Counter>();
}
