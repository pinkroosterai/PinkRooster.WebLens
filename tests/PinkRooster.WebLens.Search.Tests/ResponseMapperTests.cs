using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Search.Tests;

public class ResponseMapperTests
{
    private static readonly NormalizedQuery Query = QueryNormalizer.Normalize(new SearchQuery("cats"), 512);

    private static SearchResponse Map(string json, Action<SearchOptions>? configure = null)
    {
        var options = new SearchOptions();
        configure?.Invoke(options);
        var wire = JsonSerializer.Deserialize(json, SearxJsonContext.Default.SearxResponse)!;
        return new ResponseMapper(Options.Create(options)).Map(wire, Query, 5);
    }

    [Fact]
    public void Maps_a_real_2026_9_22_response()
    {
        var wire = JsonSerializer.Deserialize(Fixture.Read(Fixture.Search), SearxJsonContext.Default.SearxResponse)!;

        var response = Map(Fixture.Read(Fixture.Search));

        Assert.Equal(wire.Results!.Count, response.Results.Count + response.Meta.DroppedItems);
        Assert.NotEmpty(response.Results);
        Assert.All(response.Results, r =>
        {
            Assert.StartsWith("http", r.Url);
            Assert.False(string.IsNullOrWhiteSpace(r.Title));
            Assert.NotEmpty(r.Engines);
        });
        Assert.False(response.Meta.Partial);
        Assert.False(response.Meta.Cached);
        Assert.Equal(5, response.Meta.ElapsedMs);
        Assert.Contains(response.Results, r => r.PublishedAt is not null);
    }

    [Fact]
    public void Treats_zone_less_dates_as_utc()
    {
        var response = Map("""{ "results": [ { "url": "https://x.test/", "title": "t", "publishedDate": "2026-09-05T10:34:41" } ] }""");

        Assert.Equal(new DateTimeOffset(2026, 9, 5, 10, 34, 41, TimeSpan.Zero), response.Results[0].PublishedAt);
    }

    [Fact]
    public void Reports_unresponsive_engines_as_a_partial_success()
    {
        var response = Map(Fixture.Read(Fixture.Unresponsive));

        Assert.True(response.Meta.Partial);
        var failure = Assert.Single(response.Meta.EngineFailures);
        Assert.Equal("brave", failure.Engine);
        Assert.Equal("unexpected crash", failure.Reason);
        Assert.NotEmpty(response.Results);
    }

    [Fact]
    public void Tolerates_an_older_shape_and_unknown_fields()
    {
        var response = Map(Fixture.Read(Fixture.LegacyShape));

        Assert.Equal(2, response.Results.Count);
        Assert.Equal("Legacy shape", response.Results[0].Title);
        Assert.Equal(new DateTimeOffset(2019, 3, 4, 5, 6, 7, TimeSpan.Zero), response.Results[0].PublishedAt);
        Assert.Null(response.Results[0].Score);
        Assert.Equal(["brave", "google"], response.Results[1].Engines);
        Assert.Equal(["Plain text answer", "Object answer"], response.Answers.Select(a => a.Text));
        Assert.Equal("Box content", Assert.Single(response.Infoboxes).Content);
        Assert.Equal(["legacy suggestion"], response.Suggestions);
        Assert.Equal(["legacy corrected"], response.Corrections);
        Assert.Equal(["bare-engine", "brave"], response.Meta.EngineFailures.Select(f => f.Engine));
    }

    [Fact]
    public void Counts_what_it_drops()
    {
        var response = Map(Fixture.Read(Fixture.LegacyShape));

        // javascript: URL, the answer without text, the numeric answer, the string infobox.
        Assert.Equal(4, response.Meta.DroppedItems);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Drops_results_with_unusable_urls_instead_of_failing(string url)
    {
        var response = Map($$"""{ "results": [ { "url": "{{url}}", "title": "t" }, { "url": "https://ok.test/", "title": "ok" } ] }""");

        Assert.Equal("https://ok.test/", Assert.Single(response.Results).Url);
        Assert.Equal(1, response.Meta.DroppedItems);
    }

    [Fact]
    public void Keeps_query_strings_untouched()
    {
        var response = Map("""{ "results": [ { "url": "https://x.test/p?utm_source=a&sig=Ab%2FCd", "title": "t" } ] }""");

        Assert.Equal("https://x.test/p?utm_source=a&sig=Ab%2FCd", response.Results[0].Url);
    }

    [Fact]
    public void Cleans_text_strips_html_and_enforces_lengths()
    {
        var response = Map(
            """{ "results": [ { "url": "https://x.test/", "title": "T\u0000itle", "content": "<p>Hello <b>wor</b>ld</p>&amp; more   text" } ] }""",
            o => o.Sanitization.MaxSnippet = 18);

        Assert.Equal("T itle", response.Results[0].Title);
        Assert.Equal("Hello world & more", response.Results[0].Snippet);
    }

    [Fact]
    public void Keeps_html_in_snippets_only_when_stripping_is_off()
    {
        var response = Map(
            """{ "results": [ { "url": "https://x.test/", "title": "t", "content": "<b>x</b>" } ] }""",
            o => o.Sanitization.StripSnippetHtml = false);

        Assert.Equal("<b>x</b>", response.Results[0].Snippet);
    }

    [Fact]
    public void Drops_thumbnail_and_image_urls_with_unsafe_schemes_but_keeps_the_result()
    {
        var response = Map("""{ "results": [ { "url": "https://x.test/", "title": "t", "thumbnail": "data:image/png;base64,AAAA", "img_src": "https://x.test/i.png" } ] }""");

        Assert.Null(response.Results[0].ThumbnailUrl);
        Assert.Equal("https://x.test/i.png", response.Results[0].ImageUrl);
    }

    [Fact]
    public void Does_not_split_a_surrogate_pair_when_cutting()
    {
        var response = Map("""{ "results": [ { "url": "https://x.test/", "title": "ab😀cd" } ] }""", o => o.Sanitization.MaxTitle = 3);

        Assert.Equal("ab", response.Results[0].Title);
    }

    [Fact]
    public void An_empty_response_is_an_empty_success()
    {
        var response = Map("""{ "query": "cats", "results": [] }""");

        Assert.Empty(response.Results);
        Assert.False(response.Meta.Partial);
        Assert.Equal(0, response.Meta.ResultCount);
    }

    [Fact]
    public void Echoes_the_normalised_query_text_not_the_bang_compiled_one()
    {
        var query = QueryNormalizer.Normalize(new SearchQuery("cats") { Engines = ["brave"] }, 512);
        var wire = JsonSerializer.Deserialize("""{ "results": [] }""", SearxJsonContext.Default.SearxResponse)!;

        var response = new ResponseMapper(Options.Create(new SearchOptions())).Map(wire, query, 0);

        Assert.Equal("cats", response.Query);
    }
}
