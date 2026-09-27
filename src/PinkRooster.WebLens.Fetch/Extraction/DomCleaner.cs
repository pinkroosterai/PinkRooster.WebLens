using AngleSharp.Dom;

namespace PinkRooster.WebLens.Fetch;

/// <summary>Removes nodes that are never content, then the residue left around the chosen content.</summary>
internal static class DomCleaner
{
    private const string NeverContent = "script, style, noscript, template, iframe, object, embed, svg, canvas, form input, button, select, textarea";

    private const string Hidden = "[hidden], [aria-hidden='true'], [style*='display:none'], [style*='display: none'], [style*='visibility:hidden'], [style*='visibility: hidden']";

    /// <summary>Navigation, share bars, cookie and consent banners. Used when the options do not set their own list.</summary>
    public static IReadOnlyList<string> DefaultExcludeSelectors { get; } =
    [
        "nav", "[role='navigation']", "[role='dialog']", "[aria-modal='true']",
        "[class*='share']", "[class*='social']", "[class*='cookie']", "[id*='cookie']", "[class*='consent']", "[id*='consent']",
        "[class*='newsletter']", "[class*='related']", "[class*='breadcrumb']", "[class*='pagination']", "[class*='signature']",
        "[aria-label*='breadcrumb' i]", ".toc", "[class*='advert']", "[class*='promo']",
    ];

    /// <summary>
    /// Removes scripts, styles and, by default, what users cannot see, which is also a common place for instructions aimed
    /// at language models. Header, aside and footer stay: titles and bylines live there and Readability needs them.
    /// </summary>
    public static void RemoveIrrelevant(IDocument document, bool removeHidden)
    {
        RemoveAll(document, NeverContent);
        if (removeHidden)
        {
            RemoveAll(document, Hidden);
        }

        foreach (var comment in document.Descendants<IComment>().ToList())
        {
            comment.Remove();
        }
    }

    /// <summary>Removes the residue from chosen content: the caller's and site profile's selectors, then the defaults.</summary>
    public static void RemoveResidue(IElement content, IEnumerable<string> selectors)
    {
        foreach (var selector in selectors)
        {
            foreach (var element in content.QuerySelectorAll(selector).ToList())
            {
                // Never remove the content root's own ancestors' worth of text: only descendants go.
                element.Remove();
            }
        }
    }

    private static void RemoveAll(IDocument document, string selector)
    {
        foreach (var element in document.QuerySelectorAll(selector).ToList())
        {
            element.Remove();
        }
    }
}
