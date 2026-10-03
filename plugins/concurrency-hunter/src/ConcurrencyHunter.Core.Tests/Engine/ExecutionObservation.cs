using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Execution;
using ConcurrencyHunter.Heap;

namespace ConcurrencyHunter.Core.Tests.Engine;

internal sealed record ExecutionObservation(string Properties, string Queries, string Accesses, string Findings)
{
    internal static ExecutionObservation Capture(ScopeProgram scope, HeapSolution heap, ExecutionAnalysis analysis)
    {
        var input = new InterproceduralInput(scope, heap, analysis);
        var collection = InterproceduralAccesses.Collect(input, CancellationToken.None);
        var queries = new StringBuilder();
        var executions = analysis.Executions.OrderBy(execution => execution.Id, StringComparer.Ordinal).ToArray();
        foreach (var first in executions)
        {
            foreach (var second in executions)
                queries.AppendLine($"overlap {first.Id} {second.Id} {analysis.Overlaps(first.Id, second.Id)}");
            queries.AppendLine($"spawn-sites {first.Id} {Serialize(analysis.SpawnSitesOf(first.Id, analysis.CallPathPrefixes.GetValueOrDefault(first.Id) ?? []))}");
        }
        foreach (var region in heap.Regions.Keys.Order(StringComparer.Ordinal))
            queries.AppendLine($"single {region} {analysis.IsSingleObject(region)}");
        var visits = analysis.Visits.Values.SelectMany(visits => visits).Distinct()
                             .OrderBy(visit => visit.InstanceId, StringComparer.Ordinal).ThenBy(visit => visit.Segment).ToArray();
        var edges = heap.ExecutionEdges.GroupBy(edge => edge.CallerInstance, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.OrderBy(edge => edge.OperationId)
                                                                       .ThenBy(edge => edge.CalleeInstance, StringComparer.Ordinal)
                                                                       .ThenBy(edge => edge.Reason, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        foreach (var visit in visits)
        {
            var instance = heap.Instances[visit.InstanceId];
            foreach (var operation in scope.Reachable.Bodies[instance.BodyId].Blocks.SelectMany(block => block.Operations).Select(operation => operation.Id).Prepend(-1).Order())
                queries.AppendLine($"runs {visit.InstanceId} {visit.Segment} {operation} {analysis.Runs(instance.BodyId, visit.Segment, operation)}");
            foreach (var edge in edges.GetValueOrDefault(visit.InstanceId) ?? [])
                queries.AppendLine($"follow {visit.InstanceId} {visit.Segment} {edge.OperationId} {edge.CalleeInstance} {edge.Reason} {analysis.Follow(instance, visit.Segment, edge)}");
        }

        // Use the actual index before any pairing filter, including cells, wildcards and open/closed region groups.
        var accesses = collection.Accesses.Where(access => !access.IsConstructionLocal).ToArray();
        var positions = new Dictionary<Access, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < accesses.Length; index++)
            positions.TryAdd(accesses[index], index);
        var paired = new HashSet<Access>(ReferenceEqualityComparer.Instance);
        foreach (var pair in CandidateIndex.Build(accesses, heap).OrderedPairs())
        {
            queries.AppendLine($"ordered {positions[pair.First]} {positions[pair.Second]} {analysis.Ordered(pair.First, pair.Second)}");
            queries.AppendLine($"ordered {positions[pair.Second]} {positions[pair.First]} {analysis.Ordered(pair.Second, pair.First)}");
            paired.Add(pair.First);
            paired.Add(pair.Second);
        }
        var joins = visits.SelectMany(visit => heap.Instances[visit.InstanceId].Summary.Joins.Select(join => (visit.InstanceId, join.OperationId)))
                          .Distinct().OrderBy(join => join.InstanceId, StringComparer.Ordinal).ThenBy(join => join.OperationId);
        foreach (var join in joins)
            foreach (var access in paired.OrderBy(access => positions[access]))
                queries.AppendLine($"join {join.InstanceId} {join.OperationId} {positions[access]} {analysis.JoinDominates(join.InstanceId, join.OperationId, access)}");
        var pairs = InterproceduralPairing.Pair(collection.Accesses, analysis, heap);
        var marked = GapDecisions.Mark(pairs.Pairs, input, collection.Coverage.Gaps);
        var findings = ConflictFindings.Create(marked, collection.Accesses, CancellationToken.None);
        // Some queries lazily discover joins: observe the properties after querying, including UnprovenJoins.
        return new ExecutionObservation(Serialize(analysis), queries.ToString(), Serialize(collection.Accesses), Serialize(findings));
    }

    internal static string Serialize(object? value) => JsonSerializer.Serialize(Describe(value));

    private static object? Describe(object? value)
    {
        if (value is null || value is string || value is bool || value is char || value.GetType().IsPrimitive || value is decimal)
            return value;
        var type = value.GetType();
        if (type.IsEnum || value is System.Numerics.BigInteger)
            return value.ToString();
        if (value is IDictionary dictionary)
        {
            var entries = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
                entries.Add(Serialize(entry.Key), Describe(entry.Value));
            return entries;
        }
        if (value is IEnumerable sequence)
        {
            var items = sequence.Cast<object?>().Select(Describe).ToArray();
            return type.GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IReadOnlySet<>))
                ? items.OrderBy(item => JsonSerializer.Serialize(item), StringComparer.Ordinal).ToArray()
                : items;
        }
        // Read runtime types so derived selector terms, abstract values and conditions retain every field.
        var properties = new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["$type"] = type.FullName };
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                     .Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod is { } getter && (getter.IsPublic || getter.IsAssembly)))
        {
            if (value is ExecutionAnalysis && property.Name is nameof(ExecutionAnalysis.WalkVisits) or nameof(ExecutionAnalysis.WalkNodeVisits))
                continue;
            properties.Add(property.Name, Describe(property.GetValue(value)));
        }
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            properties.Add(field.Name, Describe(field.GetValue(value)));
        return properties;
    }
}
