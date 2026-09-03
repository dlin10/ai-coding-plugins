using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace EntryPointFixture;

public static class Sink
{
    public static void Hit() { }
}

public sealed class DerivedController : ControllerBase
{
    public void Get() => Sink.Hit();

    private void Hidden() => Sink.Hit();
}

[ApiController]
public sealed class AttributedController
{
    public void Post() => Sink.Hit();
}

public sealed class Routes;

public static class RouteExtensions
{
    public static void MapGet(this Routes routes, string pattern, Action handler) { }
    public static void MapPost(this Routes routes, string pattern, Action handler) { }
    public static void MapPut(this Routes routes, string pattern, Action handler) { }
    public static void MapDelete(this Routes routes, string pattern, Action handler) { }
    public static void MapPatch(this Routes routes, string pattern, Action handler) { }
    public static void MapMethods(this Routes routes, string pattern, string[] methods, Action handler) { }
}

public static class RouteRegistration
{
    public static void Register(Routes routes)
    {
        routes.MapGet("/get", () => Sink.Hit());
        routes.MapPost("/post", Post);
        routes.MapPut("/put", () => Sink.Hit());
        routes.MapDelete("/delete", Delete);
        routes.MapPatch("/patch", () => Sink.Hit());
        routes.MapMethods("/methods", ["GET"], Methods);
    }

    private static void Post() => Sink.Hit();
    private static void Delete() => Sink.Hit();
    private static void Methods() => Sink.Hit();
}

public interface IRequestHandler<TRequest, TResponse>
{
    void Handle(TRequest request);
}

public interface IRequestHandler<TRequest>
{
    void Handle(TRequest request);
}

public interface INotificationHandler<TNotification>
{
    void Handle(TNotification notification);
}

public interface IConsumer<TMessage>
{
    void Consume(TMessage message);
}

public interface IHandleMessages<TMessage>
{
    void Handle(TMessage message);
}

public interface IJob
{
    void Execute();
}

public sealed class RequestHandlerWithResponse : IRequestHandler<string, int>
{
    public void Handle(string request) => Sink.Hit();
}

public sealed class RequestHandler : IRequestHandler<string>
{
    public void Handle(string request) => Sink.Hit();
}

public sealed class NotificationHandler : INotificationHandler<string>
{
    public void Handle(string notification) => Sink.Hit();
}

public sealed class Consumer : IConsumer<string>
{
    public void Consume(string message) => Sink.Hit();
}

public sealed class MessageHandler : IHandleMessages<string>
{
    public void Handle(string message) => Sink.Hit();
}

public sealed class Hosted : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Sink.Hit();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class Background : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Sink.Hit();
        return Task.CompletedTask;
    }
}

public sealed class Job : IJob
{
    public void Execute() => Sink.Hit();
}
