using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CacheDetective.Mcp;

internal sealed record PageArguments
{
    internal const int DefaultPage = 1;
    internal const int DefaultPageSize = 50;

    [JsonPropertyName("page")]
    public int Page { get; init; } = DefaultPage;

    [JsonPropertyName("page_size")]
    public int PageSize { get; init; } = DefaultPageSize;
}

internal sealed record ListEnvelope<T>(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pages")] int Pages,
    [property: JsonPropertyName("items")] List<T> Items,
    [property: JsonPropertyName("notice")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Notice = null);

internal static class ResponseEnvelope
{
    internal const int MaximumSerializedBytes = 8 * 1024;

    internal static ListEnvelope<T> Create<T>(IReadOnlyList<T> source,
                                              PageArguments? arguments,
                                              JsonTypeInfo<ListEnvelope<T>> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(typeInfo);

        var requestedPage = arguments?.Page ?? PageArguments.DefaultPage;
        var requestedPageSize = arguments?.PageSize ?? PageArguments.DefaultPageSize;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedPage);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedPageSize);

        var regularPages = source.Count == 0 ? 0 : (int)Math.Ceiling((double)source.Count / requestedPageSize);
        if (Enumerable.Range(1, regularPages).All(page => Fits(Build(source, page, requestedPageSize, requestedPageSize), typeInfo)))
            return Build(source, requestedPage, requestedPageSize, requestedPageSize);

        const string notice = "Page size was reduced to stay under the response limit.";
        var partitions = Partition(source, requestedPageSize, notice, typeInfo);
        if (partitions is null)
            return new ListEnvelope<T>(source.Count, requestedPage, source.Count == 0 ? 0 : source.Count, [],
                $"Page omitted because one item exceeds the {MaximumSerializedBytes}-byte response limit.");

        var items = requestedPage <= partitions.Count ? partitions[requestedPage - 1] : [];
        return new ListEnvelope<T>(source.Count, requestedPage, partitions.Count, items, notice);
    }

    private static ListEnvelope<T> Build<T>(IReadOnlyList<T> source,
                                            int page,
                                            int pageSize,
                                            int requestedPageSize)
    {
        var pages = source.Count == 0 ? 0 : (int)Math.Ceiling((double)source.Count / pageSize);
        var skip = (long)(page - 1) * pageSize;
        var items = skip >= source.Count
            ? []
            : source.Skip((int)skip).Take(pageSize).ToList();
        var notice = pageSize == requestedPageSize
            ? null
            : $"Page size was reduced from {requestedPageSize} to {pageSize} to stay under "
              + $"the {MaximumSerializedBytes}-byte response limit.";

        return new ListEnvelope<T>(source.Count, page, pages, items, notice);
    }

    private static List<List<T>>? Partition<T>(IReadOnlyList<T> source, int requestedPageSize, string notice,
                                               JsonTypeInfo<ListEnvelope<T>> typeInfo)
    {
        var partitions = new List<List<T>>();
        var offset = 0;
        while (offset < source.Count)
        {
            var count = Math.Min(requestedPageSize, source.Count - offset);
            while (count > 0 && !Fits(new ListEnvelope<T>(source.Count, source.Count, source.Count,
                                                           source.Skip(offset).Take(count).ToList(), notice), typeInfo))
                count--;
            if (count == 0) return null;
            partitions.Add(source.Skip(offset).Take(count).ToList());
            offset += count;
        }
        return partitions;
    }

    private static bool Fits<T>(ListEnvelope<T> candidate, JsonTypeInfo<ListEnvelope<T>> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(candidate, typeInfo).Length <= MaximumSerializedBytes;
}
