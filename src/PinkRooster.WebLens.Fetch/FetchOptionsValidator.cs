using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Fetch;

/// <summary>Start-up checks of the fetch options, including the refusals that keep the SSRF relaxations out of real deployments.</summary>
internal sealed class FetchOptionsValidator(IHostEnvironment environment) : IValidateOptions<FetchOptions>
{
    /// <summary>The only environments where the proxy may be missing and private networks reachable.</summary>
    private bool Relaxable => environment.IsDevelopment() || environment.IsEnvironment("Testing");

    public ValidateOptionsResult Validate(string? name, FetchOptions options)
    {
        var errors = new List<string>();

        var browser = options.Browser;
        if (browser.MaxConcurrentContexts < 1)
        {
            errors.Add("WebLens:Fetch:Browser:MaxConcurrentContexts must be at least 1.");
        }

        if (browser.LaunchTimeout <= TimeSpan.Zero)
        {
            errors.Add("WebLens:Fetch:Browser:LaunchTimeout must be positive.");
        }

        if (browser.ViewportWidth < 1 || browser.ViewportHeight < 1 || browser.DeviceScaleFactor <= 0)
        {
            errors.Add("WebLens:Fetch:Browser: the viewport and device scale factor must be positive.");
        }

        if (browser.RecycleAfterContexts < 0)
        {
            errors.Add("WebLens:Fetch:Browser:RecycleAfterContexts must not be negative.");
        }

        var navigation = options.Navigation;
        var budget = options.Budget;
        if (navigation.NavigationTimeout <= TimeSpan.Zero || navigation.ReadinessTimeout <= TimeSpan.Zero || navigation.DomQuietPeriod <= TimeSpan.Zero
            || budget.OverallTimeout <= TimeSpan.Zero || budget.QueueTimeout <= TimeSpan.Zero)
        {
            errors.Add("WebLens:Fetch: every timeout must be positive.");
        }
        else if (navigation.NavigationTimeout + navigation.ReadinessTimeout > budget.OverallTimeout)
        {
            errors.Add("WebLens:Fetch: Navigation:NavigationTimeout plus Navigation:ReadinessTimeout must not exceed Budget:OverallTimeout.");
        }

        if (budget.RetryBackoff < TimeSpan.Zero)
        {
            errors.Add("WebLens:Fetch:Budget:RetryBackoff must not be negative.");
        }

        if (navigation.MinimumRenderedTextCharacters < 0 || navigation.MaxAutoScrollDistance < 0)
        {
            errors.Add("WebLens:Fetch:Navigation: MinimumRenderedTextCharacters and MaxAutoScrollDistance must not be negative.");
        }

        if (budget.NavigationAttempts < 1)
        {
            errors.Add("WebLens:Fetch:Budget:NavigationAttempts must be at least 1.");
        }

        if (options.HttpFirst.Timeout <= TimeSpan.Zero || options.HttpFirst.Timeout >= budget.OverallTimeout)
        {
            errors.Add("WebLens:Fetch:HttpFirst:Timeout must be positive and less than Budget:OverallTimeout, so the browser keeps time to fall back to.");
        }

        var capacity = options.Capacity;
        if (capacity.MaxConcurrentPerOrigin < 1 || capacity.OriginRegistryMaxEntries < 1 || capacity.FailureThreshold < 1)
        {
            errors.Add("WebLens:Fetch:Capacity: MaxConcurrentPerOrigin, OriginRegistryMaxEntries and FailureThreshold must be at least 1.");
        }

        if (capacity.FailureBreakDuration <= TimeSpan.Zero || capacity.BlockBreakDuration <= TimeSpan.Zero)
        {
            errors.Add("WebLens:Fetch:Capacity: the break durations must be positive.");
        }

        ValidateProxy(options.Security.EgressProxy, errors);
        ValidateSecurity(options, errors);

        var listing = options.Extraction.Listing;
        if (listing.MinimumRecords < 2 || listing.MinimumHeadlineCharacters < 1 || listing.MinimumCoverage is <= 0 or > 1)
        {
            errors.Add("WebLens:Fetch:Extraction:Listing: MinimumRecords must be at least 2, MinimumHeadlineCharacters at least 1, MinimumCoverage above 0 and at most 1.");
        }

        if (options.Extraction.MinimumContentCharacters < 1)
        {
            errors.Add("WebLens:Fetch:Extraction:MinimumContentCharacters must be at least 1.");
        }

        if (options.Limits.MaxRenderedHtmlBytes < 1024 || options.Limits.MaxMarkdownChars < 1)
        {
            errors.Add("WebLens:Fetch:Limits: MaxRenderedHtmlBytes must be at least 1024 and MaxMarkdownChars at least 1.");
        }

        if (options.Cache.Ttl <= TimeSpan.Zero)
        {
            errors.Add("WebLens:Fetch:Cache:Ttl must be positive.");
        }

        if (options.Diagnostics.DumpOnFailure && string.IsNullOrWhiteSpace(options.Diagnostics.DumpDirectory))
        {
            errors.Add("WebLens:Fetch:Diagnostics: DumpOnFailure needs a DumpDirectory.");
        }

        CheckSelectors("WebLens:Fetch:Extraction:DefaultExcludeSelectors", options.Extraction.DefaultExcludeSelectors, errors);
        foreach (var profile in options.SiteProfiles)
        {
            var label = $"WebLens:Fetch:SiteProfiles['{profile.Host}']";
            if (string.IsNullOrWhiteSpace(profile.Host) || Uri.CheckHostName(profile.Host) == UriHostNameType.Unknown)
            {
                errors.Add($"{label}: Host must be a host name.");
            }

            CheckSelectors(label, [.. new[] { profile.ContentSelector, profile.ReadySelector }.OfType<string>(), .. profile.ExcludeSelectors], errors);
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private void ValidateSecurity(FetchOptions options, List<string> errors)
    {
        var security = options.Security;
        if (security.RequireEgressProxy && string.IsNullOrWhiteSpace(security.EgressProxy.Server))
        {
            errors.Add("WebLens:Fetch:Security: RequireEgressProxy is set but no EgressProxy:Server is configured. The egress proxy is the SSRF boundary.");
        }

        if (!security.RequireEgressProxy && !Relaxable)
        {
            errors.Add($"WebLens:Fetch:Security:RequireEgressProxy may be false only in Development or Testing, not in {environment.EnvironmentName}.");
        }

        if (!options.Browser.ChromiumSandbox && !Relaxable)
        {
            errors.Add($"WebLens:Fetch:Browser:ChromiumSandbox may be false only in Development or Testing, not in {environment.EnvironmentName}.");
        }

        if (security.AllowPrivateNetworks && !Relaxable)
        {
            errors.Add($"WebLens:Fetch:Security:AllowPrivateNetworks may be true only in Development or Testing, not in {environment.EnvironmentName}.");
        }

        if (security.AllowedPorts.Any(port => port is < 1 or > 65535))
        {
            errors.Add("WebLens:Fetch:Security:AllowedPorts must be between 1 and 65535.");
        }

        foreach (var range in security.DeniedRanges)
        {
            if (!IPNetwork.TryParse(range, out _))
            {
                errors.Add($"WebLens:Fetch:Security:DeniedRanges: '{range}' is not a CIDR range.");
            }
        }
    }

    private static void ValidateProxy(EgressProxyOptions proxy, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(proxy.Server))
        {
            if (!string.IsNullOrEmpty(proxy.Username) || !string.IsNullOrEmpty(proxy.Password))
            {
                errors.Add("WebLens:Fetch:Security:EgressProxy: credentials are set but Server is not.");
            }

            return;
        }

        if (!Uri.TryCreate(proxy.Server, UriKind.Absolute, out var server) || server.Scheme is not ("http" or "https" or "socks5"))
        {
            errors.Add("WebLens:Fetch:Security:EgressProxy:Server must be an absolute http, https or socks5 URI.");
        }

        if (string.IsNullOrEmpty(proxy.Username) != string.IsNullOrEmpty(proxy.Password))
        {
            errors.Add("WebLens:Fetch:Security:EgressProxy: Username and Password go together.");
        }
    }

    private static void CheckSelectors(string label, IEnumerable<string> selectors, List<string> errors)
    {
        foreach (var selector in selectors)
        {
            if (!FetchRequestNormalizer.IsValidSelector(selector))
            {
                errors.Add($"{label}: '{selector}' is not a valid CSS selector.");
            }
        }
    }
}
