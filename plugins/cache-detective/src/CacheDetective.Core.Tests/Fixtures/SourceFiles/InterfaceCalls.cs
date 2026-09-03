using Microsoft.AspNetCore.Mvc;

namespace InterfaceCallFixture;

public interface IExactlyOne
{
    void Run();
}

public interface IMany
{
    void Run();
}

public interface IMissing
{
    void Run();
}

public sealed class ExactlyOne : IExactlyOne
{
    public void Run() { }
}

public sealed class ManyOne : IMany
{
    public void Run() { }
}

public sealed class ManyTwo : IMany
{
    public void Run() { }
}

public sealed class InterfaceController : ControllerBase
{
    private readonly IExactlyOne _exactlyOne = null!;
    private readonly IMany _many = null!;
    private readonly IMissing _missing = null!;

    public void Invoke()
    {
        _exactlyOne.Run();
        _many.Run();
        _missing.Run();
    }
}
