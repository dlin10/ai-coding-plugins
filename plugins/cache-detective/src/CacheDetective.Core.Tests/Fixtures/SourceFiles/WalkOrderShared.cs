namespace WalkOrderFixture;

/// <summary>The chain three entry points converge on, each at a different depth.</summary>
public static class Shared
{
    public static void S1() => S2();
    public static void S2() => S3();
    public static void S3() { }
}
