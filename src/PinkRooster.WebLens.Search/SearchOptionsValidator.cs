using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Search;

internal sealed partial class SearchOptionsValidator : IValidateOptions<SearchOptions>
{
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Type", "Content-Length", "Content-Encoding", "Transfer-Encoding", "Connection", "Accept", "Accept-Encoding", "Expect", "Upgrade",
    };

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    private static partial Regex HeaderNameRegex();

    public ValidateOptionsResult Validate(string? name, SearchOptions options)
    {
        var errors = new List<string>();

        var enabled = options.Instances.Where(i => i.Enabled).ToList();
        if (enabled.Count == 0)
        {
            errors.Add("WebLens:Search:Instances must contain at least one enabled instance.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in options.Instances)
        {
            var label = $"WebLens:Search:Instances['{instance.Name}']";
            if (string.IsNullOrWhiteSpace(instance.Name))
            {
                errors.Add("WebLens:Search:Instances: every instance needs a Name.");
            }
            else if (!seen.Add(instance.Name))
            {
                errors.Add($"{label}: duplicate instance name.");
            }

            ValidateBaseUri(instance, options.Transport.AllowInsecureHttp, label, errors);

            if (instance.Priority < 0)
            {
                errors.Add($"{label}: Priority must not be negative.");
            }

            foreach (var header in instance.Headers.Keys)
            {
                if (!HeaderNameRegex().IsMatch(header) || ReservedHeaders.Contains(header))
                {
                    errors.Add($"{label}: header name '{header}' is reserved or not a valid header name.");
                }
            }
        }

        if (options.Query.MaxLength < 1)
        {
            errors.Add("WebLens:Search:Query:MaxLength must be at least 1.");
        }

        if (options.Timeouts.Total <= TimeSpan.Zero || options.Timeouts.Attempt <= TimeSpan.Zero)
        {
            errors.Add("WebLens:Search:Timeouts: Total and Attempt must be positive.");
        }
        else if (options.Timeouts.Attempt >= options.Timeouts.Total)
        {
            errors.Add("WebLens:Search:Timeouts: Attempt must be shorter than Total.");
        }

        var routing = options.Routing;
        if (routing.MaxTotalAttempts < 1)
        {
            errors.Add("WebLens:Search:Routing:MaxTotalAttempts must be at least 1.");
        }

        if (routing.MaxInstanceAttempts < 1)
        {
            errors.Add("WebLens:Search:Routing:MaxInstanceAttempts must be at least 1.");
        }

        if (routing.CircuitFailureThreshold < 1)
        {
            errors.Add("WebLens:Search:Routing:CircuitFailureThreshold must be at least 1.");
        }

        if (routing.BreakDuration <= TimeSpan.Zero || routing.RateLimitedDefaultCooldown <= TimeSpan.Zero || routing.AccessDeniedCooldown <= TimeSpan.Zero)
        {
            errors.Add("WebLens:Search:Routing: BreakDuration and the cooldowns must be positive.");
        }

        if (options.Transport.MaxResponseBytes < 1024)
        {
            errors.Add("WebLens:Search:Transport:MaxResponseBytes must be at least 1024.");
        }

        if (options.Transport.ConnectRetryBackoff < TimeSpan.Zero)
        {
            errors.Add("WebLens:Search:Transport:ConnectRetryBackoff must not be negative.");
        }

        if (options.Capabilities.Ttl <= TimeSpan.Zero)
        {
            errors.Add("WebLens:Search:Capabilities:Ttl must be positive.");
        }

        if (options.Cache.Ttl <= TimeSpan.Zero)
        {
            errors.Add("WebLens:Search:Cache:Ttl must be positive.");
        }

        var sanitization = options.Sanitization;
        if (sanitization.MaxTitle < 1 || sanitization.MaxSnippet < 1 || sanitization.MaxUrl < 1)
        {
            errors.Add("WebLens:Search:Sanitization: maximum lengths must be positive.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateBaseUri(SearchInstanceOptions instance, bool allowInsecureHttp, string label, List<string> errors)
    {
        if (!Uri.TryCreate(instance.BaseUri, UriKind.Absolute, out var uri))
        {
            errors.Add($"{label}: BaseUri must be an absolute URI.");
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            errors.Add($"{label}: BaseUri must use http or https.");
        }
        else if (uri.Scheme == Uri.UriSchemeHttp && !allowInsecureHttp)
        {
            errors.Add($"{label}: BaseUri must use https unless Transport:AllowInsecureHttp is true.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            errors.Add($"{label}: BaseUri must not carry a query or fragment.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errors.Add($"{label}: BaseUri must not carry credentials; use Headers.");
        }
    }
}
