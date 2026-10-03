using System.Collections;
using System.Reflection;
using System.Text;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;
using ConcurrencyHunter.Ir;

namespace ConcurrencyHunter.Core.Tests.Engine;

internal sealed record HeapObservation(string Properties, string Queries, string Findings)
{
    internal static HeapObservation Capture(ScopeProgram scope, HeapSolution heap)
    {
        var queries = new StringBuilder();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var values = new HashSet<AbstractValue>();
        var fields = new HashSet<IrFieldRef>();
        var cells = new HashSet<string>(StringComparer.Ordinal);
        void Visit(object? item)
        {
            if (item is null || item is string || item.GetType().IsPrimitive || item.GetType().IsEnum || !seen.Add(item)) return;
            if (item is AbstractValue value) values.Add(value);
            if (item is IrFieldRef field) fields.Add(field);
            if (item is CapturedValue captured) cells.Add(captured.SymbolKey);
            if (item is IEnumerable items)
            {
                foreach (var child in items) Visit(child);
            }
            else
            {
                foreach (var property in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                             .Where(property => property.GetIndexParameters().Length == 0))
                    Visit(property.GetValue(item));
                foreach (var member in item.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    Visit(member.GetValue(item));
            }
        }
        var allFields = new HashSet<IrFieldRef>();
        foreach (var instance in heap.Instances.Values.OrderBy(instance => instance.Id, StringComparer.Ordinal))
        {
            seen.Clear();
            values.Clear();
            fields.Clear();
            cells.Clear();
            Visit(instance.Summary);
            cells.UnionWith(instance.Summary.Variables.Select(variable => variable.SymbolKey));
            foreach (var operation in scope.Reachable.Bodies[instance.BodyId].Blocks.SelectMany(block => block.Operations)) Visit(operation);
            allFields.UnionWith(fields);
            foreach (var value in values.OrderBy(ExecutionObservation.Serialize, StringComparer.Ordinal))
                queries.AppendLine($"value {instance.Id} {ExecutionObservation.Serialize(value)} {ExecutionObservation.Serialize(heap.Resolve(instance.Id, value))}");
            foreach (var field in fields.Where(field => field.IsStatic).OrderBy(ExecutionObservation.Serialize, StringComparer.Ordinal))
                queries.AppendLine($"static {instance.Id} {ExecutionObservation.Serialize(field)} {heap.StaticRegionOf(instance.Id, field)}");
            foreach (var cell in cells.Order(StringComparer.Ordinal))
                queries.AppendLine($"cell {instance.Id} {cell} {ExecutionObservation.Serialize(heap.Cell(instance.Id, cell))}");
        }
        foreach (var region in heap.Regions.Keys.Order(StringComparer.Ordinal).ToArray())
        {
            var names = heap.FieldsOf(region);
            queries.AppendLine($"fields {region} {ExecutionObservation.Serialize(names)}");
            foreach (var field in names.Concat(allFields.Select(field => field.Name)).Distinct().Order(StringComparer.Ordinal))
                queries.AppendLine($"field {region} {field} {ExecutionObservation.Serialize(heap.PointsTo(region, field))}");
            queries.AppendLine($"captures {region} {ExecutionObservation.Serialize(heap.DelegateCaptures(region))}");
        }
        var findings = ExecutionObservation.Capture(scope, heap, ExecutionModel.Build(scope, heap, CancellationToken.None)).Findings;
        var properties = heap.GetType().GetProperties().ToDictionary(property => property.Name,
            property => property.Name == nameof(HeapSolution.Counters)
                ? (object)heap.Counters.Where(pair => pair.Key is not HeapCounters.INSTANCE_PROCESSINGS and not HeapCounters.REFERENCE_LOOKUPS)
                                      .ToDictionary(pair => pair.Key, pair => pair.Value)
                : property.GetValue(heap));
        return new(ExecutionObservation.Serialize(properties), queries.ToString(), findings);
    }
}
