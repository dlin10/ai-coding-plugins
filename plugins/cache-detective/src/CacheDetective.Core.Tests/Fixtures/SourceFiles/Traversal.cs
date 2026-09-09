using Microsoft.AspNetCore.Mvc;

namespace TraversalFixture;

[ApiController]
public sealed class TraversalController
{
    public void Deep() => Chain.M1();

    public void Cyclic() => Cycle.A();
}

public static class Chain
{
    public static void M1() => M2();
    public static void M2() => M3();
    public static void M3() => M4();
    public static void M4() => M5();
    public static void M5() => M6();
    public static void M6() => M7();
    public static void M7() => M8();
    public static void M8() => M9();
    public static void M9() => M10();
    public static void M10() => M11();
    public static void M11() => M12();
    public static void M12() => M13();
    public static void M13() => M14();
    public static void M14() { }
}

public static class Cycle
{
    public static void A() => B();
    public static void B() => A();
}
