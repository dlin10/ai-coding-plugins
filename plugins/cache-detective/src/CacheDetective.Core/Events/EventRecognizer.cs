using CacheDetective.Graph;

namespace CacheDetective.Events;

public sealed record EventRecognizer(string Name, IReadOnlyList<string> PublisherTypeNames,
                                    IReadOnlyList<string> PublishMethods, int EventArgumentIndex,
                                    string ConsumerInterfaceName, int ConsumerArity, string HandleMethod,
                                    string HandlerKind, Confidence Confidence, int? AnnotationId)
{
    public EventRecognizer(string name, IReadOnlyList<string> publisherTypeNames,
                           IReadOnlyList<string> publishMethods, int eventArgumentIndex,
                           string consumerInterfaceName, int consumerArity, string handleMethod,
                           Confidence confidence, int? annotationId)
        : this(name, publisherTypeNames, publishMethods, eventArgumentIndex, consumerInterfaceName,
               consumerArity, handleMethod, "consumer", confidence, annotationId)
    {
    }
}

public static class EventRecognizers
{
    public static IReadOnlyList<EventRecognizer> All { get; } =
    [
        new("mediatr", ["MediatR.IPublisher", "MediatR.IMediator"], ["Publish"], 0,
            "INotificationHandler", 1, "Handle", "notification_handler", Confidence.Confirmed, null),
        new("masstransit", ["MassTransit.IPublishEndpoint", "MassTransit.IBus"], ["Publish"], 0,
            "IConsumer", 1, "Consume", "consumer", Confidence.Confirmed, null),
        new("rebus", ["Rebus.Bus.IBus"], ["Publish"], 0,
            "IHandleMessages", 1, "Handle", "message_handler", Confidence.Confirmed, null),
        new("nservicebus", ["NServiceBus.IMessageSession", "NServiceBus.IPipelineContext"], ["Publish"], 0,
            "IHandleMessages", 1, "Handle", "message_handler", Confidence.Confirmed, null)
    ];
}
