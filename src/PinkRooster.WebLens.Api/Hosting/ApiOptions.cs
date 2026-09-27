using System.Globalization;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Api.Hosting;

/// <summary>Bound from <c>WebLens:Api</c>. Key hashes and the Valkey connection string are secrets: environment or secret store only.</summary>
internal sealed class ApiOptions
{
    public List<ApiKeyOptions> Keys { get; } = [];

    /// <summary>Named rate-limit profiles; a key without one uses <c>default</c>, which exists unless configured otherwise.</summary>
    public Dictionary<string, RateLimitProfile> RateLimitProfiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The L2 cache, shared by replicas and kept across app restarts. Required outside Development and Testing.</summary>
    public string? ValkeyConnectionString { get; set; }

    /// <summary>Host backstops above the modules' own budgets, so the innermost deadline fires first: search <c>Timeouts:Total</c> + margin, fetch <c>Budget:OverallTimeout</c> + margin.</summary>
    public TimeSpan SearchRequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan FetchRequestTimeout { get; set; } = TimeSpan.FromSeconds(35);

    /// <summary>Addresses of the reverse proxies whose X-Forwarded-* headers are trusted. Empty: forwarded headers are ignored.</summary>
    public List<string> KnownProxies { get; } = [];

    public RateLimitProfile Profile(string? name) =>
        RateLimitProfiles.TryGetValue(name ?? RateLimitProfile.DefaultName, out var profile) ? profile : RateLimitProfile.Default;
}

internal sealed class ApiKeyOptions
{
    public string Id { get; set; } = "";

    /// <summary>SHA-256 of the key, 64 hex characters. The key itself is never stored or logged.</summary>
    public string Sha256 { get; set; } = "";

    public List<string> Scopes { get; } = [];
    public string? RateLimitProfile { get; set; }
}

/// <summary>Per-key limits (defaults are starting points, not measurements). Search: a token bucket. Fetch: a concurrency limit, then a token bucket.</summary>
internal sealed class RateLimitProfile
{
    public const string DefaultName = "default";

    public static RateLimitProfile Default { get; } = new();

    public int SearchPerMinute { get; set; } = 60;
    public int SearchBurst { get; set; } = 20;

    /// <summary>Lower than the browser's <c>MaxConcurrentContexts</c>, so one key cannot occupy every context.</summary>
    public int FetchConcurrency { get; set; } = 2;
    public int FetchPerMinute { get; set; } = 20;
    public int FetchBurst { get; set; } = 5;
}

internal sealed class ApiOptionsValidator(IHostEnvironment environment) : IValidateOptions<ApiOptions>
{
    public static readonly string[] KnownScopes = [Scopes.Search, Scopes.Fetch];

    public ValidateOptionsResult Validate(string? name, ApiOptions options)
    {
        var errors = new List<string>();
        var relaxable = environment.IsDevelopment() || environment.IsEnvironment("Testing");

        if (options.Keys.Count == 0 && !environment.IsDevelopment())
        {
            errors.Add("WebLens:Api:Keys must contain at least one API key outside Development.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in options.Keys)
        {
            if (string.IsNullOrWhiteSpace(key.Id) || !ids.Add(key.Id))
            {
                errors.Add($"WebLens:Api:Keys: key ids must be present and unique ('{key.Id}').");
            }

            if (key.Sha256.Length != 64 || !key.Sha256.All(Uri.IsHexDigit))
            {
                errors.Add($"WebLens:Api:Keys['{key.Id}']: Sha256 must be 64 hex characters.");
            }

            if (key.Scopes.Count == 0 || key.Scopes.Any(s => !KnownScopes.Contains(s, StringComparer.Ordinal)))
            {
                errors.Add($"WebLens:Api:Keys['{key.Id}']: Scopes must be one or more of {string.Join(", ", KnownScopes)}.");
            }

            if (key.RateLimitProfile is { } profile && profile != RateLimitProfile.DefaultName && !options.RateLimitProfiles.ContainsKey(profile))
            {
                errors.Add($"WebLens:Api:Keys['{key.Id}']: rate-limit profile '{profile}' is not defined.");
            }
        }

        foreach (var (profileName, profile) in options.RateLimitProfiles)
        {
            if (profile.SearchPerMinute < 1 || profile.SearchBurst < 1 || profile.FetchConcurrency < 1 || profile.FetchPerMinute < 1 || profile.FetchBurst < 1)
            {
                errors.Add($"WebLens:Api:RateLimitProfiles['{profileName}']: every limit must be at least 1.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.ValkeyConnectionString) && !relaxable)
        {
            errors.Add($"WebLens:Api:ValkeyConnectionString is required outside Development and Testing (the L2 cache), not in {environment.EnvironmentName}.");
        }

        if (options.SearchRequestTimeout <= TimeSpan.Zero || options.FetchRequestTimeout <= TimeSpan.Zero)
        {
            errors.Add("WebLens:Api: the request timeouts must be positive.");
        }

        foreach (var proxy in options.KnownProxies)
        {
            if (!System.Net.IPAddress.TryParse(proxy, out _))
            {
                errors.Add($"WebLens:Api:KnownProxies: '{proxy}' is not an IP address.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal static class Scopes
{
    public const string Search = "search";
    public const string Fetch = "fetch";
    public const string ClaimType = "scope";
    public const string KeyIdClaimType = "key_id";

    public static string Format(TimeSpan span) => span.TotalSeconds.ToString(CultureInfo.InvariantCulture);
}
