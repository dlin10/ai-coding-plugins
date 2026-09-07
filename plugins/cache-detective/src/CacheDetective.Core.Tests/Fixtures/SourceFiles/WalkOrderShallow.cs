using Microsoft.AspNetCore.Mvc;

namespace WalkOrderFixture;

/// <summary>Reaches the shared chain at depth 1.</summary>
[ApiController]
public sealed class ShallowController
{
    public void Enter() => Shared.S1();
}
