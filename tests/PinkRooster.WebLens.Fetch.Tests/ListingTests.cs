namespace PinkRooster.WebLens.Fetch.Tests;

/// <summary>Listing edge cases and the cost guard. The two target shapes are in <see cref="CorpusTests"/>.</summary>
public class ListingTests
{
    private static string Card(int i, string extra = "") =>
        $"<div class=\"card\"><a href=\"/story/{i}\"><h3>Story number {i} with a real headline</h3><p>Summary of story {i}, a sentence long.</p>{extra}</a></div>";

    private static string Page(string cards) =>
        $"<html><body><main><h1>Index</h1><div class=\"grid\">{cards}</div></main></body></html>";

    private static async Task<PipelineResult> RunAsync(string html, Func<FetchRequest, FetchRequest>? configure = null)
    {
        using var pipeline = Pipeline.Create();
        var request = new FetchRequest("https://corpus.test/index");
        return await pipeline.RunAsync(Pipeline.Page(html, "https://corpus.test/index"), configure is null ? request : configure(request));
    }

    [Fact]
    public async Task A_run_with_a_record_that_has_no_link_is_not_a_region_and_does_not_fail()
    {
        var cards = string.Concat(Enumerable.Range(1, 3).Select(i => Card(i))) + "<div class=\"card\"><p>An advert without a link, long enough to be text.</p></div>";

        var result = await RunAsync(Page(cards + cards.Replace("/story/", "/more/", StringComparison.Ordinal)));

        Assert.Null(result.Verdict.Error);
    }

    [Fact]
    public async Task Fewer_records_than_the_minimum_are_not_a_listing()
    {
        // Two link-heavy cards, each under the 200-character minimum but over it together: too many links for an article,
        // too few records for a listing.
        var summary = string.Concat(Enumerable.Repeat("More words inside the card link. ", 2));
        var result = await RunAsync(Page(Card(1, $"<p>{summary}</p>") + Card(2, $"<p>{summary}</p>")));

        Assert.Null(result.Extracted);
    }

    [Fact]
    public async Task Images_are_left_out_unless_asked_for()
    {
        var cards = string.Concat(Enumerable.Range(1, 5).Select(i => Card(i, $"<img src=\"/img/{i}.jpg\" alt=\"Photo {i}\">")));

        var without = await RunAsync(Page(cards));
        var with = await RunAsync(Page(cards), r => r with { IncludeImages = true });

        Assert.Equal(ContentExtractor.Listing, without.Extracted!.Strategy);
        Assert.DoesNotContain("/img/1.jpg", without.Markdown!.Markdown);
        Assert.Contains("![Photo 1](https://corpus.test/img/1.jpg)", with.Markdown!.Markdown);
    }

    [Fact]
    public async Task A_long_listing_is_cut_between_items()
    {
        var cards = string.Concat(Enumerable.Range(1, 60).Select(i => Card(i)));

        var result = await RunAsync(Page(cards), r => r with { MaxChars = 1_000 });

        Assert.True(result.Markdown!.Truncated);
        var last = result.Markdown.Markdown.TrimEnd().Split('\n')[^1];
        Assert.EndsWith("a sentence long.", last);
    }

    /// <summary>
    /// A regression guard, not the real-page cost (about 1 ms on real index pages): a generated
    /// page at MaxRenderedHtmlBytes measured 0.3–0.6 s, so 2 s catches a quadratic slip without flaking.
    /// </summary>
    [Fact]
    public void A_page_at_the_size_limit_is_scanned_in_linear_time()
    {
        using var pipeline = Pipeline.Create();
        var cards = new System.Text.StringBuilder();
        for (var i = 0; cards.Length < pipeline.Options.Limits.MaxRenderedHtmlBytes; i++)
        {
            cards.Append(Card(i));
        }

        var document = ContentExtractor.Parse(Page(cards.ToString()));
        var extractor = new ListingExtractor(Microsoft.Extensions.Options.Options.Create(pipeline.Options));
        var text = TextMeasures.TextLength(document.Body!);
        extractor.Find(document.Body!, text, includeImages: false);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var listing = extractor.Find(document.Body!, text, includeImages: false);
        watch.Stop();

        Assert.NotNull(listing);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.ElapsedMilliseconds} ms");
    }
}
