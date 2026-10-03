using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.RefReturnChainBeyondDepth;

public sealed class Slots
{
    private readonly int[] _cells = new int[4];
    public ref int Step1() => ref Step2();
    public ref int Step2() => ref Step3();
    public ref int Step3() => ref Step4();
    public ref int Step4() => ref Step5();
    public ref int Step5() => ref Step6();
    public ref int Step6() => ref Step7();
    public ref int Step7() => ref Step8();
    public ref int Step8() => ref Step9();
    public ref int Step9() => ref _cells[0];
}

[ApiController]
[Route("cases/ref-return-chain-beyond-depth")]
public sealed class SlotsController : ControllerBase
{
    private readonly Slots _slots;
    public SlotsController(Slots slots) => _slots = slots;

    [HttpPost]
    public void Post() => _slots.Step1() = 1;

    [HttpPut]
    public void Put() => _slots.Step1() = 2;
}

public static class RefReturnChainBeyondDepthCase
{
    public static IServiceCollection AddRefReturnChainBeyondDepth(this IServiceCollection services) =>
        services.AddSingleton<Slots>();
}
