using Microsoft.AspNetCore.Mvc;

namespace WalkOrderFixture;

/// <summary>Reaches the shared chain at depth 12, exactly on the limit.</summary>
[ApiController]
public sealed class DeepController
{
    public void Enter() => Descent.D1();
}

public static class Descent
{
    public static void D1() => D2();
    public static void D2() => D3();
    public static void D3() => D4();
    public static void D4() => D5();
    public static void D5() => D6();
    public static void D6() => D7();
    public static void D7() => D8();
    public static void D8() => D9();
    public static void D9() => D10();
    public static void D10() => D11();
    public static void D11() => Shared.S1();
}
