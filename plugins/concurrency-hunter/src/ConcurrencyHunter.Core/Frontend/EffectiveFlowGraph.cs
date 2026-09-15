using ConcurrencyHunter.Ir;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace ConcurrencyHunter.Frontend;

internal sealed class EffectiveFlowGraph
{
    private readonly IReadOnlyDictionary<int, IReadOnlyList<FlowEdge>> _predecessors;

    private EffectiveFlowGraph(IReadOnlyList<FlowEdge> edges)
    {
        Edges = edges;
        _predecessors = edges.GroupBy(edge => edge.Destination)
                             .ToDictionary(
                                 group => group.Key,
                                 group => (IReadOnlyList<FlowEdge>)group.OrderBy(edge => edge.Source)
                                     .ThenBy(edge => edge.Kind)
                                     .ToArray());
    }

    internal IReadOnlyList<FlowEdge> Edges { get; }

    internal IReadOnlyList<FlowEdge> Predecessors(int blockOrdinal) =>
        _predecessors.GetValueOrDefault(blockOrdinal) ?? [];

    internal static EffectiveFlowGraph Create(ControlFlowGraph graph)
    {
        var edges = new HashSet<FlowEdge>();
        foreach (var block in graph.Blocks)
        {
            AddBranch(edges, block.Ordinal, block.ConditionalSuccessor);
            AddBranch(edges, block.Ordinal, block.FallThroughSuccessor);
        }

        AddExceptionalEdges(graph.Root, graph, edges);
        return new EffectiveFlowGraph(edges.OrderBy(edge => edge.Destination)
                                           .ThenBy(edge => edge.Source)
                                           .ThenBy(edge => edge.Kind)
                                           .ToArray());
    }

    private static void AddBranch(HashSet<FlowEdge> edges, int source, ControlFlowBranch? branch)
    {
        if (branch is null)
            return;

        if (branch.FinallyRegions.Length == 0)
        {
            if (branch.Destination is not null)
                edges.Add(new FlowEdge(source, branch.Destination.Ordinal, IrEdgeKind.Explicit));
            return;
        }

        var finallyRegions = branch.FinallyRegions;
        edges.Add(new FlowEdge(source, finallyRegions[0].FirstBlockOrdinal, IrEdgeKind.FinallyEntry));
        for (var index = 0; index < finallyRegions.Length - 1; index++)
        {
            edges.Add(new FlowEdge(
                finallyRegions[index].LastBlockOrdinal,
                finallyRegions[index + 1].FirstBlockOrdinal,
                IrEdgeKind.FinallyEntry));
        }

        if (branch.Destination is not null)
        {
            edges.Add(new FlowEdge(
                finallyRegions[^1].LastBlockOrdinal,
                branch.Destination.Ordinal,
                IrEdgeKind.FinallyExit));
        }
    }

    private static void AddExceptionalEdges(ControlFlowRegion region, ControlFlowGraph graph,
                                            HashSet<FlowEdge> edges)
    {
        if (region.Kind == ControlFlowRegionKind.Try && region.EnclosingRegion is not null)
        {
            var handlers = HandlerRegions(region.EnclosingRegion).ToArray();
            for (var ordinal = region.FirstBlockOrdinal; ordinal <= region.LastBlockOrdinal; ordinal++)
            {
                if (graph.Blocks[ordinal].Kind == BasicBlockKind.Block)
                {
                    foreach (var handler in handlers)
                        edges.Add(new FlowEdge(ordinal, handler.FirstBlockOrdinal, IrEdgeKind.Exceptional));
                }
            }
        }

        foreach (var nested in region.NestedRegions)
            AddExceptionalEdges(nested, graph, edges);
    }

    private static IEnumerable<ControlFlowRegion> HandlerRegions(ControlFlowRegion parent)
    {
        foreach (var sibling in parent.NestedRegions)
        {
            if (sibling.Kind is ControlFlowRegionKind.Catch or ControlFlowRegionKind.Finally)
            {
                yield return sibling;
            }
            else if (sibling.Kind == ControlFlowRegionKind.FilterAndHandler)
            {
                foreach (var handler in sibling.NestedRegions.Where(nested =>
                             nested.Kind is ControlFlowRegionKind.Filter or ControlFlowRegionKind.Catch))
                {
                    yield return handler;
                }
            }
        }
    }

    internal sealed record FlowEdge(int Source, int Destination, IrEdgeKind Kind);
}
