// Every shape the fold must name as a set, one per method, each handing its expression to Use so a test
// can fold exactly that argument. See docs/adr/0015.
namespace FoldedSetFixture;

public static class Cases
{
    public static void Use(string value) { }

    public static void TwoBranchLocal(bool flag)
    {
        var value = "one";
        if (flag) value = "two";
        Use(value);
    }

    public static void IfElseLocal(bool flag)
    {
        string value;
        if (flag) value = "yes";
        else value = "no";
        Use(value);
    }

    public static void Conditional(bool flag) => Use(flag ? "true" : "false");

    public static void NestedConditional(bool outer, bool inner) =>
        Use(outer ? (inner ? "a" : "b") : "c");

    /// <summary>Two multi-valued parts of one composite: the result is every pairing.</summary>
    public static void Product(bool left, bool right) =>
        Use((left ? "a" : "b") + (right ? "1" : "2"));

    /// <summary>Two branches that fold to the same value are one value.</summary>
    public static void Duplicate(bool flag)
    {
        var value = "same";
        if (flag) value = "same";
        Use(value);
    }

    /// <summary>Some members nameable and one not.</summary>
    public static void Mixed(bool flag, string runtime) => Use(flag ? "known:key" : runtime);

    /// <summary>A compound assignment the fold does not see at all.</summary>
    public static void CompoundUpdate(string suffix)
    {
        var url = "start";
        url += suffix;
        Use(url);
    }

    /// <summary>A simple assignment that reads the variable it writes.</summary>
    public static void SelfAssignment(string suffix)
    {
        var url = "start";
        url = url + suffix;
        Use(url);
    }

    /// <summary>Three by three is nine, one past the cap of eight.</summary>
    public static void OverCap(int left, int right) =>
        Use((left == 0 ? "a" : left == 1 ? "b" : "c") + (right == 0 ? "1" : right == 1 ? "2" : "3"));

    /// <summary>A single-element composite built over a part that went past the cap.</summary>
    public static void CompositeOverCap(int left, int right) =>
        Use("key:" + ((left == 0 ? "a" : left == 1 ? "b" : "c") + (right == 0 ? "1" : right == 1 ? "2" : "3")));

    /// <summary>Two by four is eight, exactly the cap.</summary>
    public static void AtCap(bool left, int right) =>
        Use((left ? "a" : "b") + (right == 0 ? "1" : right == 1 ? "2" : right == 2 ? "3" : "4"));

    public static void Single(int id) => Use($"single:{id}");

    /// <summary>Four values whose ordinal order is not their source order.</summary>
    public static void Ordering(int choice) =>
        Use(choice == 0 ? "d" : choice == 1 ? "b" : choice == 2 ? "c" : "a");
}
