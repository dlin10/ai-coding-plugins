using Microsoft.CodeAnalysis;
using CacheDetective.Events;
using CacheDetective.Graph;

namespace CacheDetective.Indexing.EntryPoints;

/// <summary>Message-bus consumers. The one finder that does more than discover entry points: the consumer
/// interface names the contract it handles, so the same pass that finds the handler is the only place that
/// knows which event reaches it, and it records the <c>Consumes</c> edge — or, where the contract is still an
/// open type parameter, the unresolved row that asks the reader to name the events. That is why this finder
/// needs the graph, and why <see cref="EntryPointContext"/> carries one.</summary>
internal sealed class EventConsumerEntryPointFinder(IReadOnlyList<EventRecognizer> consumerForms) : IEntryPointFinder
{
    public void Find(INamedTypeSymbol type, EntryPointContext context, ICollection<EntryPoint> entryPoints)
    {
        foreach (var recognizer in consumerForms)
        {
            AddEventHandlingMethods(type, context, recognizer, entryPoints);
        }
    }

    private static void AddEventHandlingMethods(INamedTypeSymbol type, EntryPointContext context,
                                                EventRecognizer recognizer,
                                                ICollection<EntryPoint> entryPoints)
    {
        var graph = context.Graph;
        var implementations = new HashSet<IMethodSymbol>(MethodSymbols.Comparer);
        var interfaces = type.AllInterfaces.Where(candidate => TypeShapes.HasShape(candidate, recognizer.ConsumerInterfaceName,
                                                                                   recognizer.ConsumerArity));
        foreach (var interfaceType in interfaces)
        {
            var contract = interfaceType.TypeArguments[0];
            foreach (var member in interfaceType.GetMembers().OfType<IMethodSymbol>().Where(method => method.Name == recognizer.HandleMethod))
            {
                if (type.FindImplementationForInterfaceMember(member) is not IMethodSymbol implementation ||
                    !implementation.Locations.Any(location => location.IsInSource) || !implementations.Add(implementation))
                {
                    continue;
                }

                var handlerKind = string.IsNullOrWhiteSpace(recognizer.HandlerKind) ? "consumer" : recognizer.HandlerKind;
                var handler = EntryPointSymbols.CreateHandler(implementation, context.SolutionName, handlerKind);
                entryPoints.Add(new EntryPoint(implementation, handlerKind, []));
                var evidence = EntryPointSymbols.CreateEvidence(type.Locations.First(location => location.IsInSource));
                if (contract is ITypeParameterSymbol)
                {
                    var unresolved = graph.AddUnresolved(UnresolvedKind.Event, handler, evidence, interfaceType.ToDisplayString(),
                                                         "Open generic consumer: name its events.");
                    graph.MarkEventSite(unresolved.Id, EventSiteRole.Consume);
                    continue;
                }

                graph.AddEdge(new Consumes(new Event(EntryPointSymbols.GetFullName(contract)), handler, recognizer.Confidence, [evidence])
                {
                    AnnotationId = recognizer.AnnotationId
                });
            }
        }
    }
}
