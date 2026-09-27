using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using ReverseMarkdown.Dom;

namespace PinkRooster.WebLens.Fetch;

internal sealed record ConvertedMarkdown(string Markdown, bool Truncated);

/// <summary>
/// Removes raw HTML from the Markdown DOM before it is rendered: raw blocks and inlines become their plain
/// text, or disappear when they have none. Code is a different node type, so <c>&lt;...&gt;</c> inside code is untouched.
/// </summary>
internal static class RawHtmlPolicy
{
    public static void Apply(MarkdownDocument document)
    {
        foreach (var node in document.Descendants().ToList())
        {
            switch (node)
            {
                case MdHtmlBlock block:
                    var blockText = PlainText(block.Html);
                    if (blockText.Length == 0)
                    {
                        block.Remove();
                    }
                    else
                    {
                        var paragraph = new MdParagraph();
                        paragraph.Children.Add(new MdText(blockText));
                        block.ReplaceWith(paragraph);
                    }

                    break;
                case MdRawInline inline:
                    var inlineText = PlainText(inline.Html);
                    if (inlineText.Length == 0)
                    {
                        inline.Remove();
                    }
                    else
                    {
                        inline.ReplaceWith(new MdText(inlineText));
                    }

                    break;
            }
        }
    }

    private static string PlainText(string html) => TextMeasures.Collapse(new HtmlParser().ParseDocument(html).Body?.TextContent);
}

/// <summary>HTML content to Markdown: link and image policy on the HTML, ReverseMarkdown, raw-HTML policy, normalise, truncate.</summary>
internal sealed class MarkdownConverter(IOptions<FetchOptions> options)
{
    private readonly MarkdownOptions _options = options.Value.Markdown;

    /// <summary>A <paramref name="listing"/> may be cut between its items when a list does not fit.</summary>
    public ConvertedMarkdown Convert(IElement content, Uri finalUrl, bool includeLinks, bool includeImages, int maxChars, bool listing = false)
    {
        ApplyLinkPolicy(content, finalUrl, includeLinks);
        ApplyImagePolicy(content, finalUrl, includeImages);

        // The default flavor, on purpose: in ReverseMarkdown 6.2.1 the CommonMark and GitHub flavors hand the whole input
        // back as one raw HTML block. The default already writes fenced code and pipe tables.
        var config = new ReverseMarkdown.Config();
        config.Tags.Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass;
        config.Formatting.RemoveComments = true;
        config.Links.SmartHref = true;
        var converter = new ReverseMarkdown.Converter(config);

        var document = converter.Parse(content.OuterHtml);
        if (_options.StripRawHtml)
        {
            RawHtmlPolicy.Apply(document);
        }

        var markdown = MarkdownNormalizer.Normalize(converter.Render(document), _options.NormalizeNfc);
        return MarkdownTruncator.Truncate(markdown, maxChars, listing);
    }

    /// <summary>Links become absolute against the final URL; without links only their text stays. Script links never survive.</summary>
    private void ApplyLinkPolicy(IElement content, Uri finalUrl, bool includeLinks)
    {
        foreach (var link in content.QuerySelectorAll("a").ToList())
        {
            var href = link.GetAttribute("href");
            var target = href is null ? null : Resolve(href, finalUrl);
            if (!includeLinks || target is null)
            {
                link.Replace([.. link.ChildNodes]);
                continue;
            }

            link.SetAttribute("href", target);
            link.RemoveAttribute("title");
        }
    }

    /// <summary>Images are dropped by default, keeping their alt text; kept, they point at absolute URLs.</summary>
    private void ApplyImagePolicy(IElement content, Uri finalUrl, bool includeImages)
    {
        foreach (var image in content.QuerySelectorAll("img").ToList())
        {
            var src = image.GetAttribute("src");
            var target = src is null ? null : Resolve(src, finalUrl);
            if (!includeImages || target is null)
            {
                var alt = image.GetAttribute("alt");
                if (string.IsNullOrWhiteSpace(alt))
                {
                    image.Remove();
                }
                else
                {
                    image.Replace(content.Owner!.CreateTextNode(alt.Trim()));
                }

                continue;
            }

            image.SetAttribute("src", target);
        }
    }

    /// <summary>An absolute http(s) or mailto URL, or null for anything else (<c>javascript:</c>, <c>data:</c>, fragments).</summary>
    private string? Resolve(string raw, Uri baseUrl)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return null;
        }

        Uri? uri;
        if (_options.ResolveRelativeLinks)
        {
            Uri.TryCreate(baseUrl, trimmed, out uri);
        }
        else
        {
            Uri.TryCreate(trimmed, UriKind.Absolute, out uri);
        }

        return uri is { Scheme: "http" or "https" or "mailto" } ? uri.AbsoluteUri : null;
    }
}
