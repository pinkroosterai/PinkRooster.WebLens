using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Fetch;

/// <summary>The chosen content and what is known about the page.</summary>
internal sealed record ExtractedContent(IElement Content, string Strategy, double Quality, string? Title, PageMetadata Metadata);

/// <summary>Why a candidate was turned down, or its score when it was not.</summary>
internal sealed record QualityVerdict(bool Accepted, double Score, string Reason);

/// <summary>The quality check: a candidate must look like real content, not navigation, a stub or a challenge.</summary>
internal sealed class QualityScorer(IOptions<FetchOptions> options)
{
    private const string MeaningfulBlocks = "p, li, pre, h1, h2, h3, h4, h5, h6, table, blockquote, dd";
    private const string BlockMarkers = ".g-recaptcha, .h-captcha, .cf-turnstile, #challenge-form, #px-captcha";

    // chosenByCaller: the candidate came from an explicit content selector, so it is not refused for being mostly links;
    // whoever named the region knew it was a list. Every other check still applies.
    public QualityVerdict Score(IElement candidate, int pageTextLength, bool chosenByCaller = false)
    {
        var minimum = options.Value.Extraction.MinimumContentCharacters;
        var text = TextMeasures.TextLength(candidate);
        if (text < minimum)
        {
            return new(false, 0, $"shorter than {minimum} characters");
        }

        var linkDensity = text == 0 ? 1 : (double)TextMeasures.LinkTextLength(candidate) / text;
        if (linkDensity > 0.5 && !chosenByCaller)
        {
            return new(false, 0, "mostly link text");
        }

        if (candidate.QuerySelector(MeaningfulBlocks) is null)
        {
            return new(false, 0, "no paragraph, list, heading, code or table");
        }

        var share = pageTextLength == 0 ? 1 : (double)text / pageTextLength;
        if (pageTextLength > 5000 && share < 0.05)
        {
            return new(false, 0, "a tiny fraction of a large page");
        }

        if (candidate.QuerySelector(BlockMarkers) is not null)
        {
            return new(false, 0, "contains a challenge or CAPTCHA");
        }

        // Longer, less link-heavy content scores higher; five times the minimum counts as fully long enough.
        var length = Math.Min(1.0, text / (minimum * 5.0));
        return new(true, Math.Round((0.6 * length) + (0.4 * (1 - linkDensity)), 2), "accepted");
    }

    /// <summary>
    /// A page that is short as a whole: its cleaned body is the content when the page
    /// has some text but less than the minimum, and no challenge marker. The score stays at or below 0.3, under the lowest
    /// score an accepted candidate can get (0.32), so callers can tell it from extracted content.
    /// </summary>
    public QualityVerdict ScoreShortPage(IElement body, int pageTextLength)
    {
        var minimum = options.Value.Extraction.MinimumContentCharacters;
        if (pageTextLength == 0 || pageTextLength >= minimum || TextMeasures.TextLength(body) == 0)
        {
            return new(false, 0, "not a short page");
        }

        return body.QuerySelector(BlockMarkers) is not null
            ? new(false, 0, "contains a challenge or CAPTCHA")
            : new(true, Math.Round(0.3 * pageTextLength / minimum, 2), "short page");
    }
}

/// <summary>Parses the rendered page once, cleans it, and runs the strategy chain until a candidate passes the quality check.</summary>
internal sealed class ContentExtractor(IEnumerable<IExtractionStrategy> strategies, QualityScorer scorer, ListingExtractor listings, IOptions<FetchOptions> options)
{
    private readonly IReadOnlyList<IExtractionStrategy> _strategies = [.. strategies];
    private readonly ExtractionOptions _options = options.Value.Extraction;

    /// <summary>The one parse of the rendered HTML (SmartReader reuses it).</summary>
    public static IHtmlDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    /// <summary>The URL relative links resolve against: the page's <c>&lt;base href&gt;</c> when it has a usable one, else the final URL.</summary>
    public static Uri BaseUrl(IDocument document, Uri finalUrl) =>
        document.QuerySelector("base[href]")?.GetAttribute("href") is { } href
        && Uri.TryCreate(finalUrl, href, out var baseUrl)
        && baseUrl.Scheme is "http" or "https"
            ? baseUrl
            : finalUrl;

    /// <summary>The chosen content, or null when no strategy produced acceptable content. Cleans <paramref name="document"/> in place.</summary>
    public async Task<ExtractedContent?> ExtractAsync(IHtmlDocument document, NormalizedFetch fetch, CancellationToken ct)
    {
        var fallbackMetadata = MetaTags(document);
        var fallbackTitle = Title(document);

        DomCleaner.RemoveIrrelevant(document, _options.RemoveHidden);
        var pageText = document.Body is null ? 0 : TextMeasures.TextLength(document.Body);
        var residue = fetch.ExcludeSelectors.Concat(_options.DefaultExcludeSelectors.Count > 0 ? _options.DefaultExcludeSelectors : DomCleaner.DefaultExcludeSelectors).ToList();

        var input = new ExtractionInput(document, fetch.Url, fetch.ContentSelector);
        string? learnedTitle = null;
        PageMetadata? learnedMetadata = null;
        foreach (var strategy in _strategies)
        {
            ct.ThrowIfCancellationRequested();
            if (await strategy.ExtractAsync(input, ct) is not { } candidate)
            {
                continue;
            }

            learnedTitle ??= candidate.Title;
            learnedMetadata ??= candidate.Metadata;

            DomCleaner.RemoveResidue(candidate.Content, residue);
            var chosenByCaller = strategy is ExplicitSelectorExtractionStrategy;
            var verdict = scorer.Score(candidate.Content, pageText, chosenByCaller);
            if (verdict.Accepted)
            {
                // A region the caller named that is itself a listing is written as one.
                var content = chosenByCaller
                    && listings.Find(candidate.Content, TextMeasures.TextLength(candidate.Content), fetch.IncludeImages) is { } selected
                        ? selected.Content
                        : candidate.Content;
                return new ExtractedContent(
                    content,
                    strategy.Name,
                    verdict.Score,
                    learnedTitle ?? fallbackTitle,
                    Merge(learnedMetadata, fallbackMetadata));
            }
        }

        // Last resort for a page that is not short: an index of links. Before the
        // short-page fallback below, which cleans the body in place.
        if (pageText >= _options.MinimumContentCharacters && document.Body is { } page
            && listings.Find(page, pageText, fetch.IncludeImages) is { } listing)
        {
            return new ExtractedContent(listing.Content, Listing, listing.Coverage, learnedTitle ?? fallbackTitle, Merge(learnedMetadata, fallbackMetadata));
        }

        if (document.Body is { } body)
        {
            DomCleaner.RemoveResidue(body, residue);
            var shortPage = scorer.ScoreShortPage(body, pageText);
            if (shortPage.Accepted)
            {
                return new ExtractedContent(body, WholePage, shortPage.Score, learnedTitle ?? fallbackTitle, Merge(learnedMetadata, fallbackMetadata));
            }
        }

        return null;
    }

    /// <summary>The strategy name reported when a short page's whole body is the content.</summary>
    public const string WholePage = "whole-page";

    /// <summary>The strategy name reported when an index of links is written as a list.</summary>
    public const string Listing = "listing";

    private static string? Title(IDocument document)
    {
        var title = document.QuerySelector("meta[property='og:title']")?.GetAttribute("content") ?? document.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = document.QuerySelector("h1")?.TextContent;
        }

        return string.IsNullOrWhiteSpace(title) ? null : TextMeasures.Collapse(title);
    }

    /// <summary>What the page says about itself in its head, read before clean-up removes anything.</summary>
    private static PageMetadata MetaTags(IDocument document)
    {
        string? Meta(string selector) => document.QuerySelector(selector)?.GetAttribute("content") is { Length: > 0 } value ? value.Trim() : null;

        var published = Meta("meta[property='article:published_time']") ?? document.QuerySelector("time[datetime]")?.GetAttribute("datetime");
        return new PageMetadata(
            Meta("meta[name='author']") ?? Meta("meta[property='article:author']"),
            document.DocumentElement.GetAttribute("lang") is { Length: > 0 } lang ? lang : null,
            DateTimeOffset.TryParse(published, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date) ? date.ToUniversalTime() : null,
            Meta("meta[property='og:site_name']"),
            Meta("meta[name='description']") ?? Meta("meta[property='og:description']"));
    }

    private static PageMetadata Merge(PageMetadata? learned, PageMetadata tags) => learned is null
        ? tags
        : new PageMetadata(
            learned.Author ?? tags.Author,
            learned.Language ?? tags.Language,
            learned.PublishedAt ?? tags.PublishedAt,
            learned.SiteName ?? tags.SiteName,
            learned.Excerpt ?? tags.Excerpt);
}
