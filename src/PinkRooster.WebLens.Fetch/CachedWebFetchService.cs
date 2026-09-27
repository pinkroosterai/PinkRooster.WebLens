using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Fetch;

/// <summary>
/// The fetch cache in front of <see cref="WebFetchService"/>. Concurrent identical fetches share one browser run, which is
/// nobody's in particular: it runs under the server's budget and the cache's token (cancelled only when every waiting
/// caller has gone), and a caller's own timeout bounds only that caller's wait. Only
/// successes are stored, and never one whose readiness timed out: it may be incomplete.
/// </summary>
internal sealed class CachedWebFetchService(WebFetchService inner, HybridCache cache, IOptions<FetchOptions> options, TimeProvider time) : IWebFetchService
{
    private readonly FetchOptions _options = options.Value;

    public async Task<FetchResult> FetchMarkdownAsync(FetchRequest request, CancellationToken ct)
    {
        if (!_options.Cache.Enabled)
        {
            return await inner.FetchMarkdownAsync(request, ct);
        }

        // Validation first: an invalid request is refused, never looked up.
        var fetch = FetchRequestNormalizer.Normalize(request, _options);
        var key = Key(fetch);
        var entry = new HybridCacheEntryOptions { Expiration = _options.Cache.Ttl, LocalCacheExpiration = _options.Cache.Ttl };

        if (request.BypassCache)
        {
            // Nobody else waits on this one, so it runs under the caller's own budget.
            var fresh = await inner.FetchMarkdownAsync(request, ct);
            if (Cacheable(fresh))
            {
                await cache.SetAsync(key, fresh, entry, cancellationToken: ct);
            }

            return fresh;
        }

        using var wait = new CancellationTokenSource(fetch.Budget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, wait.Token);
        var executed = false;
        try
        {
            var result = await cache.GetOrCreateAsync(
                key,
                (inner, shared: request with { Timeout = null }),
                async (state, token) =>
                {
                    executed = true;
                    var result = await state.inner.FetchMarkdownAsync(state.shared, token);

                    // Every waiter gets it, but it must not be stored.
                    return Cacheable(result) ? result : throw new UncacheableResult(result);
                },
                entry,
                cancellationToken: linked.Token);

            return executed ? result : result with { Diagnostics = result.Diagnostics with { Cached = true } };
        }
        catch (UncacheableResult uncacheable)
        {
            return uncacheable.Result;
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new FetchException(FetchErrorKind.Timeout, "The fetch did not finish within its time budget.");
        }
    }

    public async Task<FetchResult?> TryGetCachedAsync(FetchRequest request, CancellationToken ct)
    {
        var fetch = FetchRequestNormalizer.Normalize(request, _options);
        if (!_options.Cache.Enabled || request.BypassCache)
        {
            return null;
        }

        var hit = await cache.GetOrCreateAsync(Key(fetch), static _ => ValueTask.FromResult<FetchResult?>(null), LookupOnly, cancellationToken: ct);
        return hit is null ? null : hit with { Diagnostics = hit.Diagnostics with { Cached = true } };
    }

    /// <summary>Reads L1 and L2 without running the factory or writing anything back.</summary>
    private static readonly HybridCacheEntryOptions LookupOnly = new()
    {
        Flags = HybridCacheEntryFlags.DisableUnderlyingData | HybridCacheEntryFlags.DisableLocalCacheWrite | HybridCacheEntryFlags.DisableDistributedCacheWrite,
    };

    private static bool Cacheable(FetchResult result) => !result.Diagnostics.ReadinessTimedOut;

    /// <summary>
    /// A hash of the normalised URL (lower-case host, default port and fragment gone, query order kept) and the options
    /// that change the output. Not the timeout or the bypass flag. Hashed, so no URL appears in a key.
    /// </summary>
    internal static string Key(NormalizedFetch fetch)
    {
        var url = new UriBuilder(fetch.Url) { Fragment = "" }.Uri.AbsoluteUri;
        var canonical = string.Join('\n',
            url,
            fetch.ContentSelector ?? "",
            fetch.ReadySelector ?? "",
            string.Join('\u001f', fetch.ExcludeSelectors),
            fetch.IncludeLinks ? "links" : "no-links",
            fetch.IncludeImages ? "images" : "no-images",
            fetch.MaxChars.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return "weblens:fetch:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Carries a result out of the cache factory without the cache storing it.</summary>
    private sealed class UncacheableResult(FetchResult result) : Exception("The result is not cacheable.")
    {
        public FetchResult Result { get; } = result;
    }
}
