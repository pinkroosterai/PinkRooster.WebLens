namespace PinkRooster.WebLens.Fetch;

/// <summary>What a rendered page may download besides its document, scripts, stylesheets and data.</summary>
internal enum ResourcePolicy
{
    /// <summary>No audio or video.</summary>
    NoMedia,

    /// <summary>Everything.</summary>
    Full,

    /// <summary>No audio, video, images or fonts: nothing extraction reads.</summary>
    Minimal,
}

/// <summary>Bound from <c>WebLens:Fetch</c>. Validated at start by <see cref="FetchOptionsValidator"/>.</summary>
internal sealed class FetchOptions
{
    public BrowserOptions Browser { get; } = new();
    public NavigationOptions Navigation { get; } = new();
    public BudgetOptions Budget { get; } = new();
    public CapacityOptions Capacity { get; } = new();
    public SecurityOptions Security { get; } = new();
    public ResourceOptions Resources { get; } = new();
    public ExtractionOptions Extraction { get; } = new();
    public MarkdownOptions Markdown { get; } = new();
    public LimitOptions Limits { get; } = new();
    public DiagnosticsOptions Diagnostics { get; } = new();
    public FetchCacheOptions Cache { get; } = new();
    public HttpFirstOptions HttpFirst { get; } = new();
    public List<SiteProfileOptions> SiteProfiles { get; } = [];
}

/// <summary>
/// Try a plain HTTP request through the egress proxy before the browser, and render only when the page needs it. Off in
/// the browser tests that are about the browser path.
/// </summary>
internal sealed class HttpFirstOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>The plain request's own limit inside the fetch budget; running out falls back to the browser.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);
}

internal sealed class BrowserOptions
{
    /// <summary>
    /// Null or empty runs the separate headless shell, the default since it rendered 6–24% faster with no new challenges
    /// in measured trials; <c>chromium</c> selects Chromium's new headless mode.
    /// </summary>
    public string? Channel { get; set; }
    public bool Headless { get; set; } = true;

    /// <summary>
    /// Chromium's own sandbox (L4). Playwright turns it off unless asked; WebLens asks. In a container it
    /// needs the seccomp profile in deploy/weblens; on a host that restricts user namespaces (Ubuntu 24.04) it cannot
    /// start at all, so Development and Testing may turn it off, and nothing else may.
    /// </summary>
    public bool ChromiumSandbox { get; set; } = true;

    /// <summary>Launch the browser in the background when the host starts. Off in tests that never fetch.</summary>
    public bool LaunchOnStart { get; set; } = true;

    public string Locale { get; set; } = "en-US";
    public string TimezoneId { get; set; } = "UTC";
    public int ViewportWidth { get; set; } = 1366;
    public int ViewportHeight { get; set; } = 768;
    public float DeviceScaleFactor { get; set; } = 1;

    /// <summary>Null keeps the browser's own user agent: WebLens does not pose as another client.</summary>
    public string? UserAgent { get; set; }

    public int MaxConcurrentContexts { get; set; } = 4;
    public TimeSpan LaunchTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Replace the browser after this many contexts, to bound leaked memory. Zero disables it.</summary>
    public int RecycleAfterContexts { get; set; } = 500;
}

internal sealed class NavigationOptions
{
    public TimeSpan NavigationTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ReadinessTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>300 ms, measured: 6–42% faster renders than 750 ms, with no page losing content.</summary>
    public TimeSpan DomQuietPeriod { get; set; } = TimeSpan.FromMilliseconds(300);
    public int MinimumRenderedTextCharacters { get; set; } = 200;
    public bool AutoScroll { get; set; }
    public int MaxAutoScrollDistance { get; set; } = 10_000;
}

internal sealed class BudgetOptions
{
    public TimeSpan OverallTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan QueueTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public int NavigationAttempts { get; set; } = 2;

    /// <summary>First backoff between navigation attempts when the target sent no Retry-After; doubled per attempt, jittered.</summary>
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromMilliseconds(500);
}

internal sealed class CapacityOptions
{
    public int MaxConcurrentPerOrigin { get; set; } = 2;
    public int OriginRegistryMaxEntries { get; set; } = 10_000;
    public int FailureThreshold { get; set; } = 3;
    public TimeSpan FailureBreakDuration { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan BlockBreakDuration { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>SSRF defence. The egress proxy (L3) is the boundary; the address rules here (L1, L2) are fast, friendly refusals in front of it.</summary>
internal sealed class SecurityOptions
{
    public EgressProxyOptions EgressProxy { get; } = new();

    /// <summary>Refuse to start without <see cref="EgressProxy"/>. May be false only in Development and Testing.</summary>
    public bool RequireEgressProxy { get; set; } = true;

    /// <summary>Ports a fetch may reach. Empty means the default, 80 and 443.</summary>
    public List<int> AllowedPorts { get; } = [];

    /// <summary>Turns the built-in private and special-purpose ranges off. Development and Testing only.</summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>Extra ranges (CIDR) always refused, whatever <see cref="AllowPrivateNetworks"/> says: the SearXNG network, this service's own address.</summary>
    public List<string> DeniedRanges { get; } = [];

    /// <summary>Internal DNS suffixes always refused, such as <c>.internal</c> or <c>.svc.cluster.local</c>.</summary>
    public List<string> DeniedHostSuffixes { get; } = [];
}

internal sealed class EgressProxyOptions
{
    public string? Server { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}

internal sealed class ResourceOptions
{
    public ResourcePolicy Policy { get; set; } = ResourcePolicy.NoMedia;
}

internal sealed class ExtractionOptions
{
    public int MinimumContentCharacters { get; set; } = 200;
    public bool RemoveHidden { get; set; } = true;

    /// <summary>Removed from the chosen content: navigation, share bars, cookie banners. Replaces the defaults when set.</summary>
    public List<string> DefaultExcludeSelectors { get; } = [];

    public ListingOptions Listing { get; } = new();
}

/// <summary>
/// When a page with no article counts as a listing. The values are measured against the test corpus.
/// </summary>
internal sealed class ListingOptions
{
    /// <summary>Adjacent records of one shape that make a data region.</summary>
    public int MinimumRecords { get; set; } = 3;

    /// <summary>A record's headline must be at least this long, so menu labels are not headlines.</summary>
    public int MinimumHeadlineCharacters { get; set; } = 20;

    /// <summary>The share of the page's text the records must hold for the listing to be the answer.</summary>
    public double MinimumCoverage { get; set; } = 0.5;
}

internal sealed class MarkdownOptions
{
    public bool ResolveRelativeLinks { get; set; } = true;
    public bool StripRawHtml { get; set; } = true;
    public bool NormalizeNfc { get; set; }
}

internal sealed class LimitOptions
{
    public int MaxRenderedHtmlBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>The server's Markdown cap; a caller's <c>MaxChars</c> can only lower it.</summary>
    public int MaxMarkdownChars { get; set; } = 200_000;
}

/// <summary>The fetch cache: L1 in process, L2 on Valkey when the host configures one.</summary>
internal sealed class FetchCacheOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(15);
}

internal sealed class DiagnosticsOptions
{
    /// <summary>Write the rendered HTML of a fetch that failed extraction to <see cref="DumpDirectory"/>. The files are page content: sensitive.</summary>
    public bool DumpOnFailure { get; set; }
    public string? DumpDirectory { get; set; }
}

internal sealed class SiteProfileOptions
{
    public string Host { get; set; } = "";
    public string? ContentSelector { get; set; }
    public string? ReadySelector { get; set; }
    public List<string> ExcludeSelectors { get; } = [];
}
