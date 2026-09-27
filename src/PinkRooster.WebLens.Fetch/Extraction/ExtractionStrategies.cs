using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace PinkRooster.WebLens.Fetch;

/// <summary>What a strategy proposes: a detached element holding the content, and what it learned about the page.</summary>
internal sealed record ExtractionCandidate(IElement Content, string? Title, PageMetadata? Metadata);

/// <summary>The input every strategy reads: the cleaned page. Strategies must not change it; SmartReader works on a clone.</summary>
internal sealed record ExtractionInput(IHtmlDocument Document, Uri Url, string? ContentSelector);

internal interface IExtractionStrategy
{
    string Name { get; }

    Task<ExtractionCandidate?> ExtractAsync(ExtractionInput input, CancellationToken ct);
}

/// <summary>The caller's or site profile's content selector: domain knowledge beats heuristics.</summary>
internal sealed class ExplicitSelectorExtractionStrategy : IExtractionStrategy
{
    public string Name => "explicit-selector";

    public Task<ExtractionCandidate?> ExtractAsync(ExtractionInput input, CancellationToken ct)
    {
        if (input.ContentSelector is null)
        {
            return Task.FromResult<ExtractionCandidate?>(null);
        }

        var matches = input.Document.QuerySelectorAll(input.ContentSelector);
        if (matches.Length == 0)
        {
            return Task.FromResult<ExtractionCandidate?>(null);
        }

        var container = input.Document.CreateElement("div");
        foreach (var match in matches)
        {
            container.AppendChild(match.Clone(true));
        }

        return Task.FromResult<ExtractionCandidate?>(new ExtractionCandidate(container, null, null));
    }
}

/// <summary>SmartReader (a Mozilla Readability port). Also the best source of title, byline, language and date.</summary>
internal sealed class SmartReaderExtractionStrategy : IExtractionStrategy
{
    public string Name => "smartreader";

    public async Task<ExtractionCandidate?> ExtractAsync(ExtractionInput input, CancellationToken ct)
    {
        // Readability rewrites the document it scores, so it gets its own copy of the one parse.
        var copy = (IHtmlDocument)input.Document.Clone(true);
        using var reader = new SmartReader.Reader(input.Url.AbsoluteUri, copy) { KeepClasses = true };
        var article = await reader.GetArticleAsync(ct);

        // Author only from the page's own metadata: SmartReader's byline guess picks up whatever sits near the title.
        var metadata = new PageMetadata(
            Blank(article.Author),
            Blank(article.Language),
            article.PublicationDate is { } date ? Utc(date) : null,
            Blank(article.SiteName),
            Blank(article.Excerpt));

        if (!article.IsReadable || string.IsNullOrWhiteSpace(article.Content))
        {
            // No content, but what it learned about the page still counts.
            return new ExtractionCandidate(input.Document.CreateElement("div"), Blank(article.Title), metadata);
        }

        var container = input.Document.CreateElement("div");
        container.InnerHtml = article.Content;
        return new ExtractionCandidate(container, Blank(article.Title), metadata);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>SmartReader converts to the machine's local time; an unspecified kind is taken as UTC.</summary>
    private static DateTimeOffset Utc(DateTime date) => date.Kind switch
    {
        DateTimeKind.Unspecified => new DateTimeOffset(date, TimeSpan.Zero),
        _ => new DateTimeOffset(date.ToUniversalTime(), TimeSpan.Zero),
    };
}

/// <summary><c>main</c>, <c>article</c> and their ARIA and microdata equivalents; the one with the most non-link text wins.</summary>
internal sealed class SemanticMainExtractionStrategy : IExtractionStrategy
{
    private const string Semantic = "main, article, [role='main'], [itemprop='articleBody']";

    public string Name => "semantic-main";

    public Task<ExtractionCandidate?> ExtractAsync(ExtractionInput input, CancellationToken ct)
    {
        var best = input.Document.QuerySelectorAll(Semantic)
            .Select(e => (Element: e, Score: TextMeasures.NonLinkTextLength(e)))
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        return Task.FromResult(best.Element is null || best.Score == 0
            ? null
            : new ExtractionCandidate((IElement)best.Element.Clone(true), null, null));
    }
}

/// <summary>
/// Last resort: paragraphs vote for their parent (and, at half weight, grandparent) with their non-link text; the
/// element with the most votes is the content.
/// </summary>
internal sealed class TextDensityFallbackStrategy : IExtractionStrategy
{
    public string Name => "text-density";

    public Task<ExtractionCandidate?> ExtractAsync(ExtractionInput input, CancellationToken ct)
    {
        var scores = new Dictionary<IElement, double>();
        foreach (var paragraph in input.Document.QuerySelectorAll("p, pre, li, td, blockquote"))
        {
            var length = TextMeasures.NonLinkTextLength(paragraph);
            if (length < 25)
            {
                continue;
            }

            if (paragraph.ParentElement is { } parent)
            {
                scores[parent] = scores.GetValueOrDefault(parent) + length;
                if (parent.ParentElement is { } grandparent)
                {
                    scores[grandparent] = scores.GetValueOrDefault(grandparent) + (length / 2.0);
                }
            }
        }

        var best = scores.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? input.Document.Body;
        return Task.FromResult(best is null ? null : new ExtractionCandidate((IElement)best.Clone(true), null, null));
    }
}

internal static class TextMeasures
{
    public static int TextLength(INode node) => Collapse(node.TextContent).Length;

    public static int LinkTextLength(IElement element) => element.QuerySelectorAll("a").Sum(a => Collapse(a.TextContent).Length);

    public static int NonLinkTextLength(IElement element) => Math.Max(0, TextLength(element) - LinkTextLength(element));

    public static string Collapse(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
