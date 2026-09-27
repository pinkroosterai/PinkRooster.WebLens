namespace PinkRooster.WebLens.Search;

public interface ISearchService
{
    Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken ct);

    /// <summary>The cached response for this query, or null when there is none. Never searches. Validates the query first, like <see cref="SearchAsync"/>.</summary>
    Task<SearchResponse?> TryGetCachedAsync(SearchQuery query, CancellationToken ct);
}
