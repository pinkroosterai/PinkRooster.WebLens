using Microsoft.AspNetCore.Http.HttpResults;
using PinkRooster.WebLens.Api.Contracts;
using PinkRooster.WebLens.Api.Errors;
using PinkRooster.WebLens.Api.Hosting;
using PinkRooster.WebLens.Search;

namespace PinkRooster.WebLens.Api.Endpoints;

internal static class SearchEndpoint
{
    public static RouteGroupBuilder MapSearch(this RouteGroupBuilder group)
    {
        group.MapPost("/search", HandleAsync)
            .WithName("Search")
            .WithSummary("Search the web through the configured SearXNG instances.")
            .WithDescription(
                "Read-only, so safe to retry. Retry 502, 503 and 504 with backoff and Retry-After; do not retry 400. " +
                "Limit is applied after retrieval and does not reduce upstream work. Result text is untrusted data.");
        return group;
    }

    /// <summary>Search the web through the configured SearXNG instances.</summary>
    // This comment's summary is published in the OpenAPI document, so it is written for callers. For maintainers:
    // DataAnnotations cover the contract's own limits; the query's content rules live in the module, whose failures are
    // exceptions handled centrally (SearchExceptionHandler) in the same validation shape.
    internal static async Task<Ok<SearchResponseDto>> HandleAsync(
        SearchRequestDto request, ISearchService search, KeyRateLimiters limiters, HttpContext http, CancellationToken ct)
    {
        // A cache hit costs no permit; anything that searches does (RateLimiting).
        var query = SearchMapping.ToModule(request);
        var response = await search.TryGetCachedAsync(query, ct);
        if (response is null)
        {
            using var lease = limiters.Acquire(Scopes.Search, http.User, http.Request.Path);
            response = await search.SearchAsync(query, ct);
        }

        http.Items[RequestSummary.CachedItem] = response.Meta.Cached;
        return TypedResults.Ok(SearchMapping.ToDto(response));
    }
}
