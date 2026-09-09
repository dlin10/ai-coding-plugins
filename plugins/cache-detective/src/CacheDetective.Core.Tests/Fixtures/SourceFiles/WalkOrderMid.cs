using Microsoft.AspNetCore.Mvc;

namespace WalkOrderFixture;

/// <summary>Reaches the shared chain at depth 4, below the depth limit.</summary>
[ApiController]
public sealed class MidController
{
    public void Enter() => Detour.M1();
}

public static class Detour
{
    public static void M1() => M2();
    public static void M2() => M3();
    public static void M3() => Shared.S1();
}
