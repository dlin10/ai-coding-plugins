using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;

namespace ConcurrencyHunter.Accesses;

public static partial class InterproceduralAccesses
{
    private sealed partial class ExecutionCollector
    {
        private static ReferenceCell UnknownCell(ReferenceCell cell, bool keepTerm = false) =>
            keepTerm ? cell with { Selector = ElementSelector.Unknown }
                     : cell with { Selector = ElementSelector.Unknown, SelectorTerm = null, IsTermBound = false };

        private static ReferenceParameterElement UnknownCell(ReferenceParameterElement cell, bool keepTerm = false) =>
            keepTerm ? cell with { Selector = ElementSelector.Unknown }
                     : cell with { Selector = ElementSelector.Unknown, Term = null, IsTermBound = false };

        private enum ReturnKind { Reference, Collection }
        private abstract record ReturnItem;
        private sealed record ReturnParam(int Ordinal) : ReturnItem;
        private sealed record ReturnParamCollection(ReferenceParameterElement Element) : ReturnItem;
        private sealed record ReturnCell(ReferenceCell Cell, string Instance) : ReturnItem;
        private sealed record ReturnUnproven : ReturnItem;

        /// <summary>A least fixpoint of returns in each instance's own frame. Varying indices and shifts inside a cycle
        /// widen, so recursion cannot generate an unbounded sequence of cells.</summary>
        private sealed class ReturnSummaries
        {
            private readonly HeapSolution _heap;
            private readonly Dictionary<(string Instance, ReturnKind Kind), HashSet<ReturnItem>> _items = [];
            private readonly Dictionary<(string Instance, int Operation), CallEdge[]> _calls;
            private readonly Dictionary<string, int> _components;
            private static readonly ReturnUnproven UNPROVEN = new();

            internal ReturnSummaries(HeapSolution heap, IEnumerable<CallEdge> edges)
            {
                _heap = heap;
                var graph = edges.ToArray();
                _calls = graph.GroupBy(edge => (edge.CallerInstance, edge.OperationId))
                              .ToDictionary(group => group.Key, group => group.ToArray());
                _components = Components(graph);
                foreach (var instance in heap.Instances.Keys)
                foreach (var kind in new[] { ReturnKind.Reference, ReturnKind.Collection })
                    _items.Add((instance, kind), []);

                var changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var instance in heap.Instances.Values)
                    foreach (var kind in new[] { ReturnKind.Reference, ReturnKind.Collection })
                    {
                        var targets = kind == ReturnKind.Reference ? instance.Summary.ReferenceReturns : instance.Summary.CollectionReturns;
                        foreach (var item in Map(instance, targets, []).ToArray())
                            changed |= _items[(instance.Id, kind)].Add(item);
                    }
                }
            }

            internal IEnumerable<ReturnItem> Of(string instance, ReturnKind kind) => _items[(instance, kind)];

            private static bool Constant(ElementSelector? selector, ValueTerm? term) =>
                term is ConstantTerm || selector?.IsExact == true && term is null;

            private static ReferenceCell Finite(ReferenceCell cell) =>
                cell.IsOnCollection && !Constant(cell.Selector, cell.SelectorTerm) ? UnknownCell(cell) : cell;

            private static ReferenceParameterElement Finite(ReferenceParameterElement cell) =>
                Constant(cell.Selector, cell.Term) ? cell : UnknownCell(cell);

            private IEnumerable<ReturnItem> Map(MethodInstance instance, IEnumerable<ReferenceTarget> targets,
                                                HashSet<(string Instance, int Operation, ReturnKind Kind)> mapping)
            {
                foreach (var target in targets)
                {
                    switch (target)
                    {
                        case ReferenceParameter parameter:
                            yield return new ReturnParam(parameter.Ordinal);
                            break;
                        case ReferenceParameterElement element:
                            yield return new ReturnParamCollection(Finite(element));
                            break;
                        case ReferenceCell cell:
                            yield return new ReturnCell(Finite(cell), instance.Id);
                            break;
                        case ReferenceCall call:
                            foreach (var item in Call(instance, call.OperationId, ReturnKind.Reference, mapping))
                                yield return item;
                            break;
                        case ReferenceCallCollection call:
                            foreach (var item in Call(instance, call.OperationId, ReturnKind.Collection, mapping))
                                yield return Widen(item);
                            break;
                        case ReferenceUnproven:
                            yield return UNPROVEN;
                            break;
                    }
                }
            }

            private IEnumerable<ReturnItem> Call(MethodInstance caller, int operation, ReturnKind kind,
                                                 HashSet<(string Instance, int Operation, ReturnKind Kind)> mapping)
            {
                var key = (caller.Id, operation, kind);
                if (!mapping.Add(key))
                    yield break;
                try
                {
                    var call = caller.Summary.Calls.FirstOrDefault(call => call.OperationId == operation);
                    var receiverFromCall = kind == ReturnKind.Reference && call?.Receivers.Any(value => value is CallResultValue or AwaitResultValue) == true;
                    foreach (var edge in _calls.GetValueOrDefault((caller.Id, operation)) ?? [])
                    foreach (var item in Of(edge.CalleeInstance, kind).ToArray())
                    foreach (var carried in Carry(caller, call, edge.CalleeInstance, item, mapping))
                        yield return receiverFromCall ? Widen(carried, keepTerm: true) : carried;
                }
                finally
                {
                    mapping.Remove(key);
                }
            }

            private IEnumerable<ReturnItem> Carry(MethodInstance caller, CallTransfer? call, string callee, ReturnItem item,
                                                  HashSet<(string Instance, int Operation, ReturnKind Kind)> mapping)
            {
                switch (item)
                {
                    case ReturnParam parameter:
                        foreach (var argument in call?.Arguments.Where(argument => argument.ParameterOrdinal == parameter.Ordinal) ?? [])
                        foreach (var mapped in Map(caller, argument.References, mapping))
                            yield return mapped;
                        break;
                    case ReturnParamCollection parameter:
                        foreach (var argument in call?.Arguments.Where(argument => argument.ParameterOrdinal == parameter.Element.Ordinal) ?? [])
                        {
                            var collection = argument.Collection;
                            var selector = parameter.Element.Selector;
                            var term = parameter.Element.Term;
                            // A zero shift keeps constants even inside a cycle. Across components an exact shift is finite.
                            var widen = collection?.Shift is not { } shift || shift != 0 && _components[caller.Id] == _components[callee];
                            if (widen)
                                selector = ElementSelector.Unknown;
                            else
                            {
                                var knownShift = collection!.Shift!.Value;
                                selector = selector.Shift(knownShift);
                                if (term is { } constant)
                                    term = knownShift == 0 ? constant : BindSum(constant, new ConstantTerm(knownShift, constant.Width, constant.Signed));
                            }
                            switch (collection?.Collection)
                            {
                                case ReferenceCell cell:
                                    var selected = cell with { Selector = selector, SelectorTerm = term, IsTermBound = false };
                                    yield return new ReturnCell(widen ? UnknownCell(selected) : Finite(selected), caller.Id);
                                    break;
                                case ReferenceParameterElement element:
                                    var selectedParameter = element with { Selector = selector, Term = term, IsTermBound = false };
                                    yield return new ReturnParamCollection(widen ? UnknownCell(selectedParameter) : Finite(selectedParameter));
                                    break;
                                case ReferenceCallCollection returned:
                                    foreach (var mapped in Call(caller, returned.OperationId, ReturnKind.Collection, mapping))
                                        yield return Widen(mapped);
                                    break;
                                default:
                                    yield return UNPROVEN;
                                    break;
                            }
                        }
                        break;
                    default:
                        yield return item;
                        break;
                }
            }

            private static ReturnItem Widen(ReturnItem item, bool keepTerm = false) => item switch
            {
                ReturnCell cell when cell.Cell.IsOnCollection => cell with { Cell = UnknownCell(cell.Cell, keepTerm) },
                ReturnParamCollection parameter => parameter with { Element = UnknownCell(parameter.Element, keepTerm) },
                _ => item
            };

            private Dictionary<string, int> Components(IReadOnlyList<CallEdge> edges)
            {
                var next = edges.GroupBy(edge => edge.CallerInstance).ToDictionary(group => group.Key, group => group.Select(edge => edge.CalleeInstance).ToArray());
                var previous = edges.GroupBy(edge => edge.CalleeInstance).ToDictionary(group => group.Key, group => group.Select(edge => edge.CallerInstance).ToArray());
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var finished = new List<string>();
                foreach (var root in _heap.Instances.Keys)
                {
                    var pending = new Stack<(string Instance, bool Exit)>();
                    pending.Push((root, false));
                    while (pending.TryPop(out var node))
                    {
                        if (node.Exit)
                            finished.Add(node.Instance);
                        else if (seen.Add(node.Instance))
                        {
                            pending.Push((node.Instance, true));
                            foreach (var child in next.GetValueOrDefault(node.Instance) ?? [])
                                pending.Push((child, false));
                        }
                    }
                }
                var components = new Dictionary<string, int>(StringComparer.Ordinal);
                var component = 0;
                foreach (var root in finished.AsEnumerable().Reverse())
                {
                    if (components.ContainsKey(root)) continue;
                    var pending = new Stack<string>();
                    pending.Push(root);
                    while (pending.TryPop(out var node))
                    {
                        if (!components.TryAdd(node, component)) continue;
                        foreach (var parent in previous.GetValueOrDefault(node) ?? [])
                            pending.Push(parent);
                    }
                    component++;
                }
                return components;
            }
        }
    }
}
