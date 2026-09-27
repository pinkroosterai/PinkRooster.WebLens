using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using PinkRooster.WebLens.Api.Errors;

namespace PinkRooster.WebLens.Api.Hosting;

/// <summary>
/// Per key, never per IP or any other caller-controlled string, after authentication. Search: a token
/// bucket. Fetch: a concurrency limit, then a token bucket, in that order, so a request refused for concurrency never
/// spends a token. No queueing: a refusal is immediate. 429 is only ever this limiter: a target site's refusal is a 422. A cache hit is not charged:
/// the handlers look in the cache first and take a permit only for work that searches or fetches.
/// </summary>
internal static partial class RateLimiting
{
    public static IServiceCollection AddWebLensRateLimiting(this IServiceCollection services)
    {
        services.AddSingleton<KeyRateLimiters>();
        return services;
    }

    /// <summary>Authentication runs first, so every limited request carries a key id; the fallback never shares a bucket with a key.</summary>
    public static string KeyId(ClaimsPrincipal user) => user.FindFirstValue(Scopes.KeyIdClaimType) ?? "\0anonymous";

    public static int RetryAfterSeconds(RateLimitLease lease)
    {
        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromSeconds(1);
        return (int)Math.Ceiling(Math.Max(retryAfter.TotalSeconds, 1));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Rate limit reached for key {KeyId} on {Path}; retry after {Seconds} s")]
    internal static partial void LogRejected(ILogger logger, string keyId, string path, int seconds);
}

/// <summary>A request refused by the key's own limiter. Written as <c>429 rate-limited</c> by <see cref="Errors.RateLimitExceptionHandler"/>.</summary>
internal sealed class RateLimitRefusal(int retryAfterSeconds) : Exception("This API key has sent too many requests.")
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// Every key's limiters, one per scope, shared by the <c>/v1</c> policies and the MCP tools so a key has one
/// budget whichever door it uses. Bounded by the configured keys: only authenticated requests reach a limiter.
/// </summary>
internal sealed class KeyRateLimiters(IOptions<ApiOptions> options, ILoggerFactory loggers) : IDisposable
{
    private readonly ILogger _logger = loggers.CreateLogger(typeof(RateLimiting));

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Scope, string KeyId), Lazy<RateLimiter>> _limiters = new();

    public RateLimiter For(string scope, string keyId) =>
        _limiters.GetOrAdd((scope, keyId), k => new Lazy<RateLimiter>(() => Create(k.Scope, Profile(k.KeyId)))).Value;

    /// <summary>
    /// A permit for one search or fetch that is not answered from the cache, or <see cref="RateLimitRefusal"/>. The caller
    /// holds the lease for the whole call, so the fetch concurrency limit counts it. <c>/v1</c> and the MCP tools share it.
    /// </summary>
    public RateLimitLease Acquire(string scope, ClaimsPrincipal user, string path)
    {
        var keyId = RateLimiting.KeyId(user);
        var lease = For(scope, keyId).AttemptAcquire();
        if (lease.IsAcquired)
        {
            return lease;
        }

        var seconds = RateLimiting.RetryAfterSeconds(lease);
        lease.Dispose();
        RateLimiting.LogRejected(_logger, keyId, path, seconds);
        throw new RateLimitRefusal(seconds);
    }

    /// <summary><see cref="Acquire"/> for an MCP tool call: the refusal becomes a <c>rate-limited</c> error result.</summary>
    public RateLimitLease AcquireForTool(string scope, ClaimsPrincipal user)
    {
        try
        {
            return Acquire(scope, user, McpEndpoint.Path);
        }
        catch (RateLimitRefusal refusal)
        {
            throw new ToolRefusal(ToolErrors.RateLimited(refusal.RetryAfterSeconds));
        }
    }

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values.Where(l => l.IsValueCreated))
        {
            limiter.Value.Dispose();
        }
    }

    private RateLimitProfile Profile(string keyId)
    {
        var api = options.Value;
        return api.Profile(api.Keys.FirstOrDefault(k => k.Id == keyId)?.RateLimitProfile);
    }

    private static RateLimiter Create(string scope, RateLimitProfile profile) => scope switch
    {
        // Search: a token bucket. Fetch: a concurrency limit, then a token bucket, so a request refused for concurrency never spends a token.
        Scopes.Search => new TokenBucketRateLimiter(BucketOptions(profile.SearchBurst, profile.SearchPerMinute)),
        Scopes.Fetch => RateLimiter.CreateChained(
            new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = profile.FetchConcurrency, QueueLimit = 0 }),
            new TokenBucketRateLimiter(BucketOptions(profile.FetchBurst, profile.FetchPerMinute))),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "No rate limit is defined for this scope."),
    };

    // One token every 60/perMinute seconds, up to the burst. Refilling perMinute tokens once a minute would be capped at
    // the burst, so a key would get only `burst` requests a minute.
    private static TokenBucketRateLimiterOptions BucketOptions(int burst, int perMinute) => new()
    {
        TokenLimit = burst,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromMinutes(1) / perMinute,
        QueueLimit = 0,
        AutoReplenishment = true,
    };
}
