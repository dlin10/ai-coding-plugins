using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.CapturedTargetEvaluatedBeforeValue;

public sealed class Box
{
    public int N;
}

public sealed class Label
{
    public string? Name;
}

public static class Steps
{
    public static Label Target(Box box, Label label)
    {
        box.N = 1;
        return label;
    }

    public static string Start(Box box)
    {
        Task.Run(() => { box.N++; });
        return "started";
    }
}

/// <summary>Hard negative: C# evaluates the receiver of a deconstruction's target before the value, so the receiver writes the
/// request's own box before the value starts the work on that box. The write comes before the work, and nothing evaluates the
/// receiver again after it.</summary>
[ApiController]
[Route("cases/captured-target-evaluated-before-value")]
public sealed class LabelController : ControllerBase
{
    [HttpPost]
    public string Post()
    {
        var box = new Box();
        string mark;
        (Steps.Target(box, new Label()).Name, mark) = (Steps.Start(box), "marked");
        return mark;
    }
}
