using System.ComponentModel;

namespace Demo.Web.Cases.ReadonlyStructPastDepthBound;

public struct State { public int Hits; }
public sealed class Leaf { public readonly State? Last; public State? Read() => Last; }
public sealed class N0(N1 next) { public readonly N1 Next = next; }
public sealed class N1(N2 next) { public readonly N2 Next = next; }
public sealed class N2(N3 next) { public readonly N3 Next = next; }
public sealed class N3(N4 next) { public readonly N4 Next = next; }
public sealed class N4(N5 next) { public readonly N5 Next = next; }
public sealed class N5(N6 next) { public readonly N6 Next = next; }
public sealed class N6(N7 next) { public readonly N7 Next = next; }
public sealed class N7(N8 next) { public readonly N8 Next = next; }
public sealed class N8(Leaf next) { public readonly Leaf Next = next; }

/// <summary>An opaque call reaches the readonly Nullable past the depth bound; its effect only reads that storage.</summary>
public sealed class Store
{
    private readonly N0 _chain;
    public Store(Leaf leaf) { _chain = new N0(new N1(new N2(new N3(new N4(new N5(new N6(new N7(new N8(leaf))))))))); }
    public void Touch() => TypeDescriptor.GetClassName(this);
}
public sealed class FirstWorker(Store store) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) { store.Touch(); return Task.CompletedTask; }
}
public sealed class SecondWorker(Leaf leaf) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) { leaf.Read(); return Task.CompletedTask; }
}
public static class ReadonlyStructPastDepthBoundCase
{
    public static IServiceCollection AddReadonlyStructPastDepthBound(this IServiceCollection services) =>
        services.AddSingleton<Leaf>().AddSingleton<Store>().AddHostedService<FirstWorker>().AddHostedService<SecondWorker>();
}
