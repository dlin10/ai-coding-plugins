using Google.Protobuf;
using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.GeneratedModelReturnsArgument;

public sealed class Item
{
    public int Count;
}

/// <summary>The singleton builds its <c>Item</c> in the constructor, so every action reaches the same one.</summary>
public sealed class Box
{
    public Item Item { get; }

    public Box() => Item = new Item();
}

/// <summary>Counts through <c>ProtoPreconditions.CheckNotNull</c>, a library member no built-in or project model describes. The generated
/// model says it returns its argument and touches nothing, so the increment lands on the singleton's <c>Item</c> and the action meets
/// itself there; without the model the call's unknown effect would stand for a pair on the same field.</summary>
[ApiController]
[Route("cases/generated-model-returns-argument")]
public sealed class BoxController : ControllerBase
{
    private readonly Box _box;

    public BoxController(Box box) => _box = box;

    [HttpPost]
    public void Post() => ProtoPreconditions.CheckNotNull(_box.Item, "item").Count++;
}

public static class GeneratedModelReturnsArgumentCase
{
    public static IServiceCollection AddGeneratedModelReturnsArgument(this IServiceCollection services) => services.AddSingleton<Box>();
}
