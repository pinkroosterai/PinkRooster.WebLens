namespace PinkRooster.WebLens.Search;

/// <summary>
/// Applies <see cref="SearchQuery.Limit"/> last, so a cache placed between this and <see cref="SearchService"/>
/// can serve any limit.
/// </summary>
internal sealed class LimitingSearchService(ISearchService inner) : ISearchService
{
    public async Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken ct) => Limit(await inner.SearchAsync(query, ct), query);

    public async Task<SearchResponse?> TryGetCachedAsync(SearchQuery query, CancellationToken ct) =>
        await inner.TryGetCachedAsync(query, ct) is { } response ? Limit(response, query) : null;

    private static SearchResponse Limit(SearchResponse response, SearchQuery query)
    {
        if (response.Results.Count <= query.Limit)
        {
            return response;
        }

        var results = response.Results.Take(query.Limit).ToList();
        return response with { Results = results, Meta = response.Meta with { ResultCount = results.Count } };
    }
}
