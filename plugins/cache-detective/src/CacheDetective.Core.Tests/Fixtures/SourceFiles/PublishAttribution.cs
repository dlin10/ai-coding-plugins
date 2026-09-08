// Publish sites whose event type is named by a caller rather than by the body that contains the call.
// See docs/adr/0017.
using Microsoft.AspNetCore.Mvc;

namespace MediatR
{
    public interface IPublisher { void Publish<T>(T @event); }
    public interface INotificationHandler<T> { void Handle(T @event); }
}

namespace AttributionFixture
{

public sealed class OrderPlaced;
public sealed class OrderShipped;
public sealed class OwnEvent;
public sealed class DeepEvent;
public sealed class LateEvent;
public sealed class UnreachableEvent;
public sealed class ExhaustedEvent;
public sealed class NamelessEvent;
public sealed class LeftEvent;
public sealed class RightEvent;
public sealed class CrossServiceEvent;

/// <summary>The shared helper: it publishes whatever it is handed and names nothing itself.</summary>
public sealed class EventPublisher(MediatR.IPublisher bus)
{
    public void Send(object @event) => bus.Publish(@event);
}

/// <summary>Two callers, two event types. Each edge belongs to its own caller.</summary>
public sealed class PlacedController(EventPublisher publisher) : ControllerBase
{
    public void Place() => publisher.Send(new OrderPlaced());
}

public sealed class ShippedController(EventPublisher publisher) : ControllerBase
{
    public void Ship() => publisher.Send(new OrderShipped());
}

/// <summary>Constructs the event in its own body, so it names it itself.</summary>
public sealed class OwnController(MediatR.IPublisher bus) : ControllerBase
{
    public void Publish() => bus.Publish(new OwnEvent());
}

/// <summary>A two-hop chain: only the method that named the type is attributed.</summary>
public sealed class MiddleHop(EventPublisher publisher)
{
    public void Forward(object @event) => publisher.Send(@event);
}

public sealed class DeepController(MiddleHop middle) : ControllerBase
{
    public void Deep() => middle.Forward(new DeepEvent());
}

/// <summary>Reached from an entry point only through a chain of calls, so the walk meets it well after the
/// helper has been expanded.</summary>
public sealed class LateController(LateStepOne step) : ControllerBase
{
    public void Late() => step.One();
}

public sealed class LateStepOne(LateStepTwo next)
{
    public void One() => next.Two();
}

public sealed class LateStepTwo(LateStepThree next)
{
    public void Two() => next.Three();
}

public sealed class LateStepThree(EventPublisher publisher)
{
    public void Three() => publisher.Send(new LateEvent());
}

/// <summary>No entry point reaches this: it is a plain class the walk never meets.</summary>
public sealed class UnreachablePublisher(EventPublisher publisher)
{
    public void Publish() => publisher.Send(new UnreachableEvent());
}

/// <summary>Names nothing concrete: the argument is a parameter of a type nothing narrows.</summary>
public sealed class NamelessController(EventPublisher publisher) : ControllerBase
{
    public void Nameless(object payload) => publisher.Send(payload);
}

/// <summary>One caller naming two types on the same expression.</summary>
public sealed class BranchController(EventPublisher publisher) : ControllerBase
{
    public void Branch(bool flag) => publisher.Send(flag ? new LeftEvent() : (object)new RightEvent());
}

/// <summary>A publisher and a consumer in the same solution, so the hop can be followed.</summary>
public sealed class CrossServiceController(EventPublisher publisher) : ControllerBase
{
    public void Raise() => publisher.Send(new CrossServiceEvent());
}

public sealed class CrossServiceConsumer : MediatR.INotificationHandler<CrossServiceEvent>
{
    public void Handle(CrossServiceEvent @event) { }
}

/// <summary>A helper of its own, so the exhausted branch below does not land on every other site.</summary>
public sealed class MixedPublisher(MediatR.IPublisher bus)
{
    public void Send(object @event) => bus.Publish(@event);
}

public sealed class MixedDirectController(MixedPublisher publisher) : ControllerBase
{
    public void Direct() => publisher.Send(new MixedDirectEvent());
}

public sealed class MixedDirectEvent;

// Six parameter hops above MixedPublisher.Send, one more than the recovery may take, so this branch
// runs out while the direct caller above resolves.
public sealed class Hop1(MixedPublisher next) { public void Go(object @event) => next.Send(@event); }
public sealed class Hop2(Hop1 next) { public void Go(object @event) => next.Go(@event); }
public sealed class Hop3(Hop2 next) { public void Go(object @event) => next.Go(@event); }
public sealed class Hop4(Hop3 next) { public void Go(object @event) => next.Go(@event); }
public sealed class Hop5(Hop4 next) { public void Go(object @event) => next.Go(@event); }

public sealed class HopController(Hop5 next) : ControllerBase
{
    public void Go() => next.Go(new ExhaustedEvent());
}

/// <summary>A direct publish — no helper, so recovery happens at the site itself — whose conditional names
/// a type in one arm and nothing in the other. The known arm produces an edge; the arm that named nothing
/// must still leave a row, or a partly successful recovery loses it silently.</summary>
public abstract class BranchBase;
public sealed class KnownBranchEvent : BranchBase;
public sealed class OtherBranchEvent : BranchBase;

public sealed class UnknownBranchSource
{
    public BranchBase Get() => new OtherBranchEvent();
}

public sealed class MixedBranchController(MediatR.IPublisher bus, UnknownBranchSource source) : ControllerBase
{
    public void Branch(bool flag) => bus.Publish(flag ? new KnownBranchEvent() : source.Get());
}

}
