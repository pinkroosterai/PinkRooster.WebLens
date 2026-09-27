namespace PinkRooster.WebLens.Search;

internal enum RoutingPolicy { HealthAwareRoundRobin, PriorityFailover, RoundRobin }

internal enum SearchHttpMethod { Post, Get }

internal enum MissingCapabilityBehavior { SkipInstance, SendAnyway }

/// <summary>Bound from <c>WebLens:Search</c>. Validated at start by <see cref="SearchOptionsValidator"/>.</summary>
internal sealed class SearchOptions
{
    public List<SearchInstanceOptions> Instances { get; } = [];
    public QueryOptions Query { get; } = new();
    public RoutingOptions Routing { get; } = new();
    public TimeoutOptions Timeouts { get; } = new();
    public TransportOptions Transport { get; } = new();
    public CapabilityOptions Capabilities { get; } = new();
    public SanitizationOptions Sanitization { get; } = new();
    public SearchCacheOptions Cache { get; } = new();
}

/// <summary>The search cache: L1 in process, L2 on Valkey when the host configures one.</summary>
internal sealed class SearchCacheOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(5);
}

internal sealed class SearchInstanceOptions
{
    public string Name { get; set; } = "";
    public string BaseUri { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class QueryOptions
{
    public int MaxLength { get; set; } = 512;
}

internal sealed class RoutingOptions
{
    public RoutingPolicy Policy { get; set; } = RoutingPolicy.HealthAwareRoundRobin;
    public int MaxInstanceAttempts { get; set; } = 3;
    public int MaxTotalAttempts { get; set; } = 4;
    public TimeSpan RateLimitedDefaultCooldown { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan AccessDeniedCooldown { get; set; } = TimeSpan.FromMinutes(1);
    public int CircuitFailureThreshold { get; set; } = 3;
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}

internal sealed class TimeoutOptions
{
    public TimeSpan Total { get; set; } = TimeSpan.FromSeconds(8);
    public TimeSpan Attempt { get; set; } = TimeSpan.FromSeconds(4);
}

internal sealed class TransportOptions
{
    public SearchHttpMethod Method { get; set; } = SearchHttpMethod.Post;
    public int MaxResponseBytes { get; set; } = 2 * 1024 * 1024;
    public TimeSpan PooledConnectionLifetime { get; set; } = TimeSpan.FromMinutes(5);
    public bool AllowInsecureHttp { get; set; }

    /// <summary>Jittered delay before the single same-instance retry of a connection-establishment failure. Zero disables the delay.</summary>
    public TimeSpan ConnectRetryBackoff { get; set; } = TimeSpan.FromMilliseconds(200);
}

internal sealed class CapabilityOptions
{
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(10);
    public MissingCapabilityBehavior MissingCapabilityBehavior { get; set; } = MissingCapabilityBehavior.SkipInstance;
}

internal sealed class SanitizationOptions
{
    public int MaxTitle { get; set; } = 300;
    public int MaxSnippet { get; set; } = 1000;
    public int MaxUrl { get; set; } = 2048;
    public bool StripSnippetHtml { get; set; } = true;
}
