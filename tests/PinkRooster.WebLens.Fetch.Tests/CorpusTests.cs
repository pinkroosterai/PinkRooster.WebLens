namespace PinkRooster.WebLens.Fetch.Tests;

/// <summary>
/// The extraction regression corpus: hand-written pages with the expected outcome, strategy and minimum quality.
/// Run on every SmartReader, AngleSharp, ReverseMarkdown or extraction-rule change; a package moves only when this passes.
/// </summary>
public class CorpusTests
{
    public sealed record Expectation(string Page, string Strategy, double MinimumQuality, string[] MustContain, string[] MustNotContain)
    {
        public override string ToString() => Page;
    }

    public static TheoryData<Expectation> Extracted => new()
    {
        new Expectation("article-news.html", "smartreader", 0.9,
            ["Saltmere's long-delayed tramway", "## A Route Years in the Making", "> "],
            ["cookie", "Share on", "Related"]),
        new Expectation("docs-page.html", "smartreader", 0.9,
            ["```csharp", "if (count < 10)", "| Name | Type | Description |", "`RingBuffer<T>`"],
            ["On this page"]),
        new Expectation("forum-thread.html", "smartreader", 0.9,
            ["two hundred miles", "> the top lid zipper"],
            ["Popular threads"]),
        new Expectation("product-page.html", "smartreader", 0.8,
            ["## Specifications", "| Capacity | 38 L |", "Removable aluminum frame sheet"],
            ["Add to cart"]),
        new Expectation("consent-wall.html", "smartreader", 0.9,
            ["double rainbow"],
            ["We value your privacy", "Accept"]),
        new Expectation("article-about-captchas.html", "smartreader", 0.9,
            ["reCAPTCHA"],
            []),
        new Expectation("hidden-injection.html", "smartreader", 0.9,
            ["Concurrent namespace"],
            ["HIDDEN-MARKER-42", "IGNORE ALL PREVIOUS INSTRUCTIONS"]),
    };

    [Theory]
    [MemberData(nameof(Extracted))]
    public async Task Pages_with_content_are_extracted_with_the_expected_strategy_and_quality(Expectation expected)
    {
        using var pipeline = Pipeline.Create();

        var result = await pipeline.RunAsync(Pipeline.Page(Corpus.Read(expected.Page), "https://corpus.test/section/" + expected.Page));

        Assert.Null(result.Verdict.Error);
        Assert.NotNull(result.Extracted);
        Assert.Equal(expected.Strategy, result.Extracted.Strategy);
        Assert.True(result.Extracted.Quality >= expected.MinimumQuality, $"quality {result.Extracted.Quality}");
        var markdown = result.Markdown!.Markdown;
        Assert.All(expected.MustContain, s => Assert.Contains(s, markdown));
        Assert.All(expected.MustNotContain, s => Assert.DoesNotContain(s, markdown, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("challenge-cloudflare.html", FetchErrorKind.BotChallenge, "cloudflare")]
    [InlineData("waf-akamai.html", FetchErrorKind.BotChallenge, "akamai")]
    [InlineData("captcha-recaptcha.html", FetchErrorKind.CaptchaRequired, "recaptcha")]
    [InlineData("captcha-hcaptcha.html", FetchErrorKind.CaptchaRequired, "hcaptcha")]
    [InlineData("waf-datadome.html", FetchErrorKind.CaptchaRequired, "datadome")]
    public async Task Challenge_pages_are_reported_and_never_extracted(string page, FetchErrorKind kind, string provider)
    {
        using var pipeline = Pipeline.Create();

        var result = await pipeline.RunAsync(Pipeline.Page(Corpus.Read(page), status: 403));

        Assert.Equal(kind, result.Verdict.Error);
        Assert.Equal(provider, result.Verdict.Block.Provider);
        Assert.Null(result.Extracted);
    }

    [Fact]
    public async Task A_page_that_is_only_site_chrome_has_no_content()
    {
        using var pipeline = Pipeline.Create();

        var result = await pipeline.RunAsync(Pipeline.Page(Corpus.Read("empty-shell.html")));

        Assert.Null(result.Verdict.Error);
        Assert.Null(result.Extracted);
    }

    [Fact]
    public async Task A_link_aggregator_front_page_is_a_listing_of_every_story()
    {
        using var pipeline = Pipeline.Create();

        var result = await pipeline.RunAsync(Pipeline.Page(Corpus.Read("listing-tinker-news.html"), "https://tinker.test/news"));

        Assert.Equal(ContentExtractor.Listing, result.Extracted!.Strategy);
        var items = Items(result.Markdown!.Markdown);
        Assert.Equal(30, items.Count);
        Assert.All(items, line => Assert.Matches(@"^\[[^\]]+\]\(https?://[^)]+\) · .*\d+ points by \w+ \d+ hours ago .*\d+ comments$", line));
        Assert.StartsWith("[A tiny garbage collector written in one weekend](https://kestrel.dev/", items[0]);
    }

    [Fact]
    public async Task A_news_front_page_is_a_listing_of_every_card_once_with_headline_and_summary_apart()
    {
        using var pipeline = Pipeline.Create();

        var result = await pipeline.RunAsync(Pipeline.Page(Corpus.Read("listing-harbor-ledger.html"), "https://corpus.test/news"));

        Assert.Equal(ContentExtractor.Listing, result.Extracted!.Strategy);
        var markdown = result.Markdown!.Markdown;
        var items = Items(markdown);
        Assert.Equal(10, items.Count);
        Assert.Equal(10, items.Select(i => i[..i.IndexOf(']', StringComparison.Ordinal)]).Distinct().Count());
        Assert.Contains("[Harbour bridge reopens after two-year repair](https://corpus.test/news/articles/bridge-reopens) · Engineers say the new deck will carry twice the traffic of the old one. · 3 hrs ago · Local", items);
        Assert.Contains("## Top stories", markdown);
        Assert.Contains("## More news", markdown);
        Assert.DoesNotContain("placeholder.png", markdown);
    }

    [Theory]
    [InlineData("empty-shell.html")]
    [InlineData("chrome-only-divs.html")]
    public async Task Site_chrome_is_never_a_listing(string page)
    {
        using var pipeline = Pipeline.Create();

        var result = await pipeline.RunAsync(Pipeline.Page(Corpus.Read(page)));

        Assert.Null(result.Verdict.Error);
        Assert.Null(result.Extracted);
    }

    /// <summary>The list items of a Markdown listing, markers stripped (adjacent lists alternate -, * and +).</summary>
    private static List<string> Items(string markdown) =>
        [.. markdown.Split('\n').Where(l => l.Length > 2 && l[0] is '-' or '*' or '+' && l[1] == ' ').Select(l => l[2..])];

    [Fact]
    public async Task A_region_named_by_the_caller_is_returned_even_when_it_is_mostly_links()
    {
        using var pipeline = Pipeline.Create();
        var page = Pipeline.Page(Corpus.Read("listing-harbor-ledger.html"), "https://corpus.test/news");

        var result = await pipeline.RunAsync(page, new FetchRequest("https://corpus.test/news") { ContentSelector = "main" });

        // Named by the caller, and itself a listing: written as one.
        Assert.NotNull(result.Extracted);
        Assert.Equal("explicit-selector", result.Extracted.Strategy);
        Assert.Contains("[Harbour bridge reopens after two-year repair](https://corpus.test/news/articles/bridge-reopens) · Engineers say", result.Markdown!.Markdown);
    }

    [Fact]
    public void A_region_named_by_the_caller_skips_only_the_link_density_rule()
    {
        using var pipeline = Pipeline.Create();
        var links = string.Concat(Enumerable.Range(1, 20).Select(i => $"<li><a href=\"/s{i}\">Story number {i} with a headline of some length</a></li>"));
        var listing = ContentExtractor.Parse($"<html><body><ul>{links}</ul></body></html>").Body!;
        var challenge = ContentExtractor.Parse($"<html><body><ul>{links}</ul><div class=\"g-recaptcha\"></div></body></html>").Body!;
        var empty = ContentExtractor.Parse("<html><body><ul><li><a href=\"/a\">A</a></li></ul></body></html>").Body!;

        Assert.False(pipeline.Scorer.Score(listing, 1000).Accepted);
        Assert.True(pipeline.Scorer.Score(listing, 1000, chosenByCaller: true).Accepted);
        Assert.False(pipeline.Scorer.Score(challenge, 1000, chosenByCaller: true).Accepted);
        Assert.False(pipeline.Scorer.Score(empty, 1000, chosenByCaller: true).Accepted);
    }

    [Fact]
    public async Task A_page_that_is_short_as_a_whole_returns_its_body_with_a_low_score()
    {
        using var pipeline = Pipeline.Create();

        var result = await pipeline.RunAsync(Pipeline.Page(Corpus.Read("short-page.html")));

        Assert.Null(result.Verdict.Error);
        Assert.NotNull(result.Extracted);
        Assert.Equal(ContentExtractor.WholePage, result.Extracted.Strategy);
        Assert.True(result.Extracted.Quality <= 0.3, $"quality {result.Extracted.Quality}");
        Assert.Contains("kept for use in examples", result.Markdown!.Markdown);
        Assert.Contains("[Read more](https://corpus.test/about)", result.Markdown.Markdown);
    }

    [Fact]
    public async Task Metadata_comes_from_the_page_and_dates_are_utc()
    {
        using var pipeline = Pipeline.Create();

        var result = await pipeline.RunAsync(Pipeline.Page(Corpus.Read("article-news.html")));

        var metadata = result.Extracted!.Metadata;
        Assert.Equal("Mara Ellison", metadata.Author);
        Assert.Equal("en", metadata.Language);
        Assert.Equal("The Harbor Ledger", metadata.SiteName);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 9, 30, 0, TimeSpan.Zero), metadata.PublishedAt);
        Assert.Equal(TimeSpan.Zero, metadata.PublishedAt!.Value.Offset);
        Assert.Equal("Saltmere's New Tram Line Opens After Four-Year Wait", result.Extracted.Title);
    }

    [Fact]
    public async Task Links_are_absolute_against_the_final_url()
    {
        using var pipeline = Pipeline.Create();

        var result = await pipeline.RunAsync(Pipeline.Page(Corpus.Read("forum-thread.html"), "https://corpus.test/forum/thread/42"));

        Assert.Contains("](https://corpus.test/gear-reviews)", result.Markdown!.Markdown);
    }
}
