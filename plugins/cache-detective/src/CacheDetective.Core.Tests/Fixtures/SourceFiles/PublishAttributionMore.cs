// More callers of the shared helper in PublishAttribution.cs, in a second document so that the order the
// caller search returns them in can be perturbed by the order the documents were added. See docs/adr/0014.
using Microsoft.AspNetCore.Mvc;

namespace AttributionFixture
{

public sealed class AlphaEvent;
public sealed class ZuluEvent;

public sealed class AlphaController(EventPublisher publisher) : ControllerBase
{
    public void Alpha() => publisher.Send(new AlphaEvent());
}

public sealed class ZuluController(EventPublisher publisher) : ControllerBase
{
    public void Zulu() => publisher.Send(new ZuluEvent());
}

}
