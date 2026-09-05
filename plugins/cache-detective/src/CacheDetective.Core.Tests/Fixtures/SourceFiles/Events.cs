using Microsoft.AspNetCore.Mvc;

namespace MediatR
{
    public interface IPublisher { void Publish<T>(T @event); }
    public interface IMediator { void Publish<T>(T @event); }
    public interface INotificationHandler<T> { void Handle(T @event); }
}

namespace MassTransit
{
    public interface IPublishEndpoint { void Publish<T>(T @event); }
    public interface IBus { void Publish<T>(T @event); }
    public interface IConsumer<T> { void Consume(T @event); }
}

namespace Rebus.Bus
{
    public interface IBus { void Publish<T>(T @event); }
}

namespace Rebus.Handlers
{
    public interface IHandleMessages<T> { void Handle(T @event); }
}

namespace NServiceBus
{
    public interface IMessageSession { void Publish<T>(T @event); }
    public interface IPipelineContext { void Publish<T>(T @event); }
    public interface IHandleMessages<T> { void Handle(T @event); }
}

namespace EventFixture
{
    public interface MyBus { void Publish<T>(T @event); }
    public interface IMyConsumer<T> { void Consume(T @event); }

    public sealed class PublishedEvent;
    public sealed class CustomEvent;
    public sealed class LonelyEvent;
    public abstract class EventBase;
    public sealed class RecoveredEvent : EventBase;
    public record ConcreteBaseEvent;
    public sealed record InterfaceRecoveredEvent : ConcreteBaseEvent;
    public record BranchBaseEvent;
    public sealed record BranchLeftEvent : BranchBaseEvent;
    public sealed record BranchRightEvent : BranchBaseEvent;
    public sealed record SealedPropertyEvent;
    public interface IEventForwarder { void Forward(ConcreteBaseEvent @event); }

    public sealed class EventForwarder(MediatR.IPublisher publisher) : IEventForwarder
    {
        public void Forward(ConcreteBaseEvent @event) => publisher.Publish(@event);
    }

    public sealed class PublishController : ControllerBase
    {
        public void MediatRPublisher(MediatR.IPublisher bus) => bus.Publish(new PublishedEvent());
        public void MediatRMediator(MediatR.IMediator bus) => bus.Publish(new PublishedEvent());
        public void MassTransitEndpoint(MassTransit.IPublishEndpoint bus) => bus.Publish(new PublishedEvent());
        public void MassTransitBus(MassTransit.IBus bus) => bus.Publish(new PublishedEvent());
        public void RebusBus(Rebus.Bus.IBus bus) => bus.Publish(new PublishedEvent());
        public void NServiceBusSession(NServiceBus.IMessageSession bus) => bus.Publish(new PublishedEvent());
        public void NServiceBusPipeline(NServiceBus.IPipelineContext bus) => bus.Publish(new PublishedEvent());
        public void Custom(MyBus bus) => bus.Publish(new CustomEvent());
        public void Lonely(MediatR.IPublisher bus) => bus.Publish(new LonelyEvent());
        public void Recover(MediatR.IPublisher bus) => PublishBase(bus, new RecoveredEvent());
        public void RecoverThroughInterface(IEventForwarder forwarder) => forwarder.Forward(new InterfaceRecoveredEvent());
        public void Conditional(MediatR.IPublisher bus, bool condition) => bus.Publish(condition ? (BranchBaseEvent)new BranchLeftEvent() : new BranchRightEvent());
        public void AssignedLocal(MediatR.IPublisher bus, bool condition)
        {
            BranchBaseEvent @event;
            if (condition) @event = new BranchLeftEvent(); else @event = new BranchRightEvent();
            bus.Publish(@event);
        }
        public BranchBaseEvent Property => new BranchLeftEvent();
        public SealedPropertyEvent SealedProperty => new SealedPropertyEvent();
        public void PropertyPublish(MediatR.IPublisher bus) => bus.Publish(Property);
        public void SealedPropertyPublish(MediatR.IPublisher bus) => bus.Publish(SealedProperty);

        private static void PublishBase(MediatR.IPublisher bus, EventBase @event) => bus.Publish(@event);
    }

    public sealed class MediatRConsumer : MediatR.INotificationHandler<PublishedEvent>
    {
        public void Handle(PublishedEvent @event) { }
    }

    public sealed class MassTransitConsumer : MassTransit.IConsumer<PublishedEvent>
    {
        public void Consume(PublishedEvent @event) { }
    }

    public sealed class RebusConsumer : Rebus.Handlers.IHandleMessages<PublishedEvent>
    {
        public void Handle(PublishedEvent @event) { }
    }

    public sealed class NServiceBusConsumer : NServiceBus.IHandleMessages<PublishedEvent>
    {
        public void Handle(PublishedEvent @event) { }
    }

    public sealed class CustomConsumer : IMyConsumer<CustomEvent>
    {
        public void Consume(CustomEvent @event) { }
    }

    public sealed class OpenConsumer<T> : MassTransit.IConsumer<T>
    {
        public void Consume(T @event) { }
    }
}
