using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Fetch;

/// <summary>A listing found on a page: the list markup to convert, and the share of the page's text its records hold.</summary>
internal sealed record Listing(IElement Content, int Records, double Coverage);

/// <summary>
/// An index page (a news front page, Hacker News) as one Markdown line per record. Finds data
/// regions the way MDR (Mining Data Records) does: a parent whose children repeat with the same tag and
/// class signature, a record being one child or a fixed group of up to <see cref="MaxRecordSpan"/> adjacent children.
/// Innermost regions win, so a page's sections are not mistaken for records. The result is list markup the existing
/// converter turns into Markdown, so links, images, the no-raw-HTML policy and truncation work as for articles.
/// </summary>
internal sealed class ListingExtractor(IOptions<FetchOptions> options)
{
    /// <summary>The widest record, in adjacent elements: Hacker News uses three (title row, subtext row, spacer).</summary>
    private const int MaxRecordSpan = 3;

    private static readonly HashSet<string> ChromeElements = new(["nav", "header", "footer", "aside"], StringComparer.Ordinal);
    private static readonly HashSet<string> ChromeRoles = new(["navigation", "banner", "contentinfo"], StringComparer.OrdinalIgnoreCase);
    private static readonly string[] ChromeWords = ["footer", "menu", "nav"];

    private const string Headings = "h1, h2, h3, h4, h5, h6";

    // Parsed once: parsing a selector per element was most of the cost on large pages.
    private static readonly CssSelectorParser Parser = new();
    private static readonly ISelector HeadingSelector = Parser.ParseSelector(Headings)!;
    private static readonly ISelector LinkSelector = Parser.ParseSelector("a[href]")!;

    private ListingOptions Settings => options.Value.Extraction.Listing;

    /// <summary>The listing in <paramref name="root"/>, or null when its records hold too little of <paramref name="pageText"/> characters.</summary>
    public Listing? Find(IElement root, int pageText, bool includeImages)
    {
        var regions = new List<Region>();
        Collect(root, regions);
        regions = Innermost(regions);
        if (regions.Count == 0 || pageText == 0)
        {
            return null;
        }

        var document = root.Owner!;
        var content = document.CreateElement("div");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var records = 0;
        var covered = 0;
        foreach (var region in regions)
        {
            var list = document.CreateElement("ul");
            foreach (var record in region.Records)
            {
                covered += record.Elements.Sum(CollapsedLength);
                if (Item(document, record, includeImages, seen) is { } item)
                {
                    list.AppendChild(item);
                    records++;
                }
            }

            if (list.ChildElementCount == 0)
            {
                continue;
            }

            if (Heading(region.Parent) is { } heading)
            {
                var title = document.CreateElement("h2");
                title.TextContent = heading;
                content.AppendChild(title);
            }

            content.AppendChild(list);
        }

        var coverage = Math.Min(1.0, (double)covered / pageText);
        return records > 0 && coverage >= Settings.MinimumCoverage ? new Listing(content, records, Math.Round(coverage, 2)) : null;
    }

    private void Collect(IElement element, List<Region> regions)
    {
        if (IsChrome(element))
        {
            return;
        }

        if (Best(element) is { } region)
        {
            regions.Add(region);
        }

        foreach (var child in element.Children)
        {
            Collect(child, regions);
        }
    }

    /// <summary>
    /// Never a listing: site chrome, by element, role, or a class or id that says so (chrome built from divs). A plain
    /// check, not a selector: it runs on every element.
    /// </summary>
    private static bool IsChrome(IElement element)
    {
        if (ChromeElements.Contains(element.LocalName) || (element.GetAttribute("role") is { } role && ChromeRoles.Contains(role)))
        {
            return true;
        }

        var names = element.GetAttribute("class") + " " + element.Id;
        foreach (var word in ChromeWords)
        {
            if (names.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The longest run of repeated records among this element's children, preferring the narrowest span.</summary>
    private Region? Best(IElement parent)
    {
        var children = parent.Children.ToArray();
        if (children.Length < Settings.MinimumRecords)
        {
            return null;
        }

        var signatures = children.Select(Signature).ToArray();
        Region? best = null;
        for (var span = 1; span <= MaxRecordSpan; span++)
        {
            for (var start = 0; start + span <= children.Length; start++)
            {
                // Count each run once, from where it starts: linear in the number of children.
                if (start >= span && SameShape(signatures, start - span, start, span))
                {
                    continue;
                }

                var count = 1;
                while (start + ((count + 1) * span) <= children.Length && SameShape(signatures, start, start + (count * span), span))
                {
                    count++;
                }

                if (count < Settings.MinimumRecords || (best is not null && count <= best.Records.Count))
                {
                    continue;
                }

                var records = new List<Record>(count);
                for (var i = 0; i < count; i++)
                {
                    var elements = children[(start + (i * span))..(start + ((i + 1) * span))];
                    if (HeadlineLink(elements) is not { } headline)
                    {
                        break;
                    }

                    records.Add(new Record(elements, headline.Link, headline.Headline));
                }

                if (records.Count == count)
                {
                    best = new Region(parent, records);
                }
            }
        }

        return best;
    }

    private static bool SameShape(string[] signatures, int a, int b, int span)
    {
        for (var i = 0; i < span; i++)
        {
            if (signatures[a + i] != signatures[b + i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Tag, test id and sorted classes: two cards of one kind share it, whatever their content or id. Generated
    /// per-element classes (CSS-in-JS hashes such as <c>jbJzqN</c>: mixed case, no separator) are left out, since they
    /// differ between cards of the same kind.
    /// </summary>
    private static string Signature(IElement element) =>
        element.LocalName + "#" + element.GetAttribute("data-testid") + "."
        + string.Join('.', (element.GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(c => !IsGenerated(c)).Order(StringComparer.Ordinal));

    private static bool IsGenerated(string token) =>
        !token.Contains('-', StringComparison.Ordinal) && !token.Contains('_', StringComparison.Ordinal)
        && token.Any(char.IsUpper) && token.Any(char.IsLower);

    /// <summary>The record's headline link: the link around or inside its heading, else its longest link long enough to be a headline.</summary>
    private (IElement Link, string Headline)? HeadlineLink(IElement[] record)
    {
        foreach (var element in record)
        {
            if (First(element, HeadingSelector) is { } heading && TextMeasures.Collapse(heading.TextContent) is { Length: > 0 } text
                && (Closest(heading, LinkSelector) ?? First(heading, LinkSelector)) is { } around
                && text.Length >= Settings.MinimumHeadlineCharacters)
            {
                return (around, text);
            }
        }

        (IElement Link, string Headline)? longest = null;
        foreach (var link in record.SelectMany(e => e.DescendantsAndSelf<IElement>().Where(d => LinkSelector.Match(d, null))))
        {
            var text = TextMeasures.Collapse(link.TextContent);
            if (text.Length >= Settings.MinimumHeadlineCharacters && (longest is null || text.Length > longest.Value.Headline.Length))
            {
                longest = (link, text);
            }
        }

        return longest;
    }

    /// <summary>One list item: the headline as a link, then the record's other text, piece by piece.</summary>
    private static IElement? Item(IDocument document, Record record, bool includeImages, HashSet<string> seen)
    {
        // The link as written, fragment dropped: the same story linked twice on one page is written the same way.
        var href = record.Link.GetAttribute("href")!;
        var fragment = href.IndexOf('#', StringComparison.Ordinal);
        if (!seen.Add(fragment < 0 ? href : href[..fragment]))
        {
            return null;
        }

        var item = document.CreateElement("li");
        var link = document.CreateElement("a");
        link.SetAttribute("href", href);
        link.TextContent = record.Headline;
        item.AppendChild(link);

        var pieces = record.Elements.SelectMany(e => Pieces(e, record.Headline)).Where(p => p.Any(char.IsLetter)).ToList();
        if (pieces.Count > 0)
        {
            item.AppendChild(document.CreateTextNode(" · " + string.Join(" · ", pieces)));
        }

        if (includeImages && record.Elements.SelectMany(e => e.QuerySelectorAll("img[alt]:not([alt=''])")).FirstOrDefault() is { } image)
        {
            item.AppendChild(document.CreateTextNode(" "));
            item.AppendChild(image.Clone(false));
        }

        return item;
    }

    /// <summary>
    /// The record's text apart from its headline, in page order: each element that holds text of its own is one piece
    /// (with what is inside it), so a summary, an age and a section stay apart but "12 points by marlo" stays whole.
    /// </summary>
    private static IEnumerable<string> Pieces(IElement element, string headline)
    {
        var own = element.ChildNodes.OfType<IText>().Any(t => !string.IsNullOrWhiteSpace(t.Data));
        if (own)
        {
            var text = TextMeasures.Collapse(element.TextContent);
            var rest = TextMeasures.Collapse(text.Replace(headline, "", StringComparison.Ordinal));
            if (rest.Length > 0)
            {
                yield return rest;
            }

            yield break;
        }

        foreach (var child in element.Children)
        {
            if (TextMeasures.Collapse(child.TextContent) == headline)
            {
                continue;
            }

            foreach (var piece in Pieces(child, headline))
            {
                yield return piece;
            }
        }
    }

    /// <summary>The heading just before the region: a heading sibling before it, or before its parent.</summary>
    private static string? Heading(IElement parent)
    {
        for (var node = parent; node is not null && node.LocalName != "body"; node = node.ParentElement)
        {
            for (var previous = node.PreviousElementSibling; previous is not null; previous = previous.PreviousElementSibling)
            {
                if (HeadingSelector.Match(previous, null))
                {
                    return TextMeasures.Collapse(previous.TextContent);
                }

                if (TextMeasures.TextLength(previous) > 0)
                {
                    break;
                }
            }

            if (node.PreviousElementSibling is not null)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Drops every region that holds another inside one of its records (a page's sections around its card grids), by one
    /// walk up from each region: linear in regions times depth, where comparing every pair was quadratic.
    /// </summary>
    private static List<Region> Innermost(List<Region> regions)
    {
        var owner = new Dictionary<IElement, Region>();
        foreach (var region in regions)
        {
            foreach (var element in region.Records.SelectMany(r => r.Elements))
            {
                owner.TryAdd(element, region);
            }
        }

        var outer = new HashSet<Region>(ReferenceEqualityComparer.Instance);
        foreach (var region in regions)
        {
            for (var node = region.Parent; node is not null; node = node.ParentElement)
            {
                if (owner.TryGetValue(node, out var around) && !ReferenceEquals(around, region))
                {
                    outer.Add(around);
                }
            }
        }

        return [.. regions.Where(r => !outer.Contains(r))];
    }

    private static IElement? First(IElement root, ISelector selector) =>
        root.Descendants<IElement>().FirstOrDefault(e => selector.Match(e, null));

    private static IElement? Closest(IElement element, ISelector selector)
    {
        for (var node = element; node is not null; node = node.ParentElement)
        {
            if (selector.Match(node, null))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>Text length as <see cref="TextMeasures.TextLength"/> counts it (whitespace runs as one space), without building the string.</summary>
    private static int CollapsedLength(INode node)
    {
        var length = 0;
        var pendingSpace = false;
        foreach (var text in node.DescendantsAndSelf<IText>())
        {
            foreach (var c in text.Data)
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = length > 0;
                }
                else
                {
                    length += pendingSpace ? 2 : 1;
                    pendingSpace = false;
                }
            }
        }

        return length;
    }

    private sealed record Record(IElement[] Elements, IElement Link, string Headline);

    private sealed record Region(IElement Parent, List<Record> Records);
}
