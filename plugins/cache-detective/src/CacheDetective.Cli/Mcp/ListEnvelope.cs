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

        for (var pageSize = requestedPageSize; pageSize > 0; pageSize--)
        {
            var candidate = Build(source, requestedPage, pageSize, requestedPageSize);
            if (JsonSerializer.SerializeToUtf8Bytes(candidate, typeInfo).Length <= MaximumSerializedBytes)
            {
                return candidate;
            }
        }

        var notice = $"Page omitted because one item exceeds the {MaximumSerializedBytes}-byte response limit.";
        return new ListEnvelope<T>(source.Count, requestedPage, source.Count == 0 ? 0 : source.Count, [], notice);
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
}
