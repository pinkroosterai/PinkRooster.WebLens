using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace PinkRooster.WebLens.Fetch;

/// <summary>A validated fetch with the site profile merged in and the caller's limits clamped to the server's.</summary>
internal sealed record NormalizedFetch(
    Uri Url,
    string? ContentSelector,
    string? ReadySelector,
    IReadOnlyList<string> ExcludeSelectors,
    bool IncludeLinks,
    bool IncludeImages,
    int MaxChars,
    TimeSpan Budget)
{
    /// <summary><c>scheme://host:port</c>: the key of the origin registry and the unit of per-origin limits.</summary>
    public string Origin => $"{Url.Scheme}://{Url.IdnHost}:{Url.Port}".ToLowerInvariant();
}

/// <summary>
/// The one place a fetch request's content rules live: URL syntax and scheme, selector count, length and
/// syntax. The host maps a failure's <see cref="FetchException.Field"/> to its own field names.
/// </summary>
internal static class FetchRequestNormalizer
{
    public const int MaxUrlLength = 2048;
    public const int MaxSelectorLength = 200;
    public const int MaxExcludeSelectors = 10;

    public static NormalizedFetch Normalize(FetchRequest request, FetchOptions options)
    {
        var url = ParseUrl(request.Url);

        var profile = options.SiteProfiles.FirstOrDefault(p => string.Equals(p.Host, url.Host, StringComparison.OrdinalIgnoreCase));

        var contentSelector = Selector(request.ContentSelector, nameof(FetchRequest.ContentSelector)) ?? profile?.ContentSelector;
        var readySelector = Selector(request.ReadySelector, nameof(FetchRequest.ReadySelector)) ?? profile?.ReadySelector;

        var excludes = new List<string>();
        if (request.ExcludeSelectors is { } requested)
        {
            if (requested.Count > MaxExcludeSelectors)
            {
                throw Invalid(nameof(FetchRequest.ExcludeSelectors), $"At most {MaxExcludeSelectors} exclude selectors are allowed.");
            }

            foreach (var raw in requested)
            {
                if (Selector(raw, nameof(FetchRequest.ExcludeSelectors)) is { } selector)
                {
                    excludes.Add(selector);
                }
            }
        }

        if (profile is not null)
        {
            excludes.AddRange(profile.ExcludeSelectors);
        }

        if (request.MaxChars is < 1)
        {
            throw Invalid(nameof(FetchRequest.MaxChars), "MaxChars must be 1 or greater.");
        }

        if (request.Timeout is { } requestedTimeout && requestedTimeout <= TimeSpan.Zero)
        {
            throw Invalid(nameof(FetchRequest.Timeout), "The timeout must be positive.");
        }

        var maxChars = Math.Min(request.MaxChars ?? int.MaxValue, options.Limits.MaxMarkdownChars);
        var budget = request.Timeout is { } timeout && timeout < options.Budget.OverallTimeout ? timeout : options.Budget.OverallTimeout;

        return new NormalizedFetch(url, contentSelector, readySelector, excludes, request.IncludeLinks, request.IncludeImages, maxChars, budget);
    }

    /// <summary>Whether AngleSharp accepts the CSS selector. Used here and by options validation.</summary>
    public static bool IsValidSelector(string selector)
    {
        try
        {
            new HtmlParser().ParseDocument("").QuerySelector(selector);
            return true;
        }
        catch (DomException)
        {
            return false;
        }
    }

    private static Uri ParseUrl(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw Invalid(nameof(FetchRequest.Url), "The URL is required.");
        }

        if (text.Length > MaxUrlLength)
        {
            throw Invalid(nameof(FetchRequest.Url), $"The URL is longer than {MaxUrlLength} characters.");
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || string.IsNullOrEmpty(url.Host))
        {
            throw Invalid(nameof(FetchRequest.Url), "The URL must be absolute, such as 'https://example.org/page'.");
        }

        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            throw Invalid(nameof(FetchRequest.Url), "Only http and https URLs can be fetched.");
        }

        return url;
    }

    private static string? Selector(string? raw, string field)
    {
        var selector = raw?.Trim();
        if (string.IsNullOrEmpty(selector))
        {
            return null;
        }

        if (selector.Length > MaxSelectorLength)
        {
            throw Invalid(field, $"A selector may be at most {MaxSelectorLength} characters.");
        }

        if (!IsValidSelector(selector))
        {
            throw Invalid(field, "The selector is not valid CSS.");
        }

        return selector;
    }

    private static FetchException Invalid(string field, string message) => new(FetchErrorKind.InvalidRequest, message) { Field = field };
}
