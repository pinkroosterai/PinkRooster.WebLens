using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Search;

/// <summary>
/// The search cache, between <see cref="LimitingSearchService"/> and <see cref="SearchService"/>: the key is the normalised
/// query without <c>Limit</c>, so one cached response serves every limit. Only successes are stored, partial ones
/// included (they are flagged). Concurrent identical searches share one execution, which runs under the module's own
/// budget and the cache's token (cancelled only when every waiting caller has gone).
/// </summary>
internal sealed class CachedSearchService(SearchService inner, HybridCache cache, IOptions<SearchOptions> options) : ISearchService
{
    private readonly SearchOptions _options = options.Value;

    public async Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        if (!_options.Cache.Enabled)
        {
            return await inner.SearchAsync(query, ct);
        }

        // Validation first: an invalid query is refused, never looked up.
        var normalized = QueryNormalizer.Normalize(query, _options.Query.MaxLength);
        var executed = false;
        var entry = new HybridCacheEntryOptions { Expiration = _options.Cache.Ttl, LocalCacheExpiration = _options.Cache.Ttl };

        var response = await cache.GetOrCreateAsync(
            Key(normalized),
            (inner, query),
            async (state, token) =>
            {
                executed = true;
                return await state.inner.SearchAsync(state.query, token);
            },
            entry,
            cancellationToken: ct);

        return executed ? response : response with { Meta = response.Meta with { Cached = true } };
    }

    public async Task<SearchResponse?> TryGetCachedAsync(SearchQuery query, CancellationToken ct)
    {
        var normalized = QueryNormalizer.Normalize(query, _options.Query.MaxLength);
        if (!_options.Cache.Enabled)
        {
            return null;
        }

        var hit = await cache.GetOrCreateAsync(Key(normalized), static _ => ValueTask.FromResult<SearchResponse?>(null), LookupOnly, cancellationToken: ct);
        return hit is null ? null : hit with { Meta = hit.Meta with { Cached = true } };
    }

    /// <summary>Reads L1 and L2 without running the factory or writing anything back.</summary>
    private static readonly HybridCacheEntryOptions LookupOnly = new()
    {
        Flags = HybridCacheEntryFlags.DisableUnderlyingData | HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite,
    };

    /// <summary>A hash of the fields sent upstream (text with its bangs, categories, language, page, time range, safe-search), so no query text appears in a key.</summary>
    internal static string Key(NormalizedQuery query)
    {
        var canonical = string.Join('\n', query.Fields.Select(f => $"{f.Key}={f.Value}"));
        return "weblens:search:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
