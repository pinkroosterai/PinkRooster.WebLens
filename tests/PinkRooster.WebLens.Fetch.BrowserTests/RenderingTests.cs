namespace PinkRooster.WebLens.Fetch.BrowserTests;

/// <summary>The real pipeline in real Chromium against the local fixture site.</summary>
public class RenderingTests
{
    [Fact]
    public async Task A_static_article_becomes_markdown_with_diagnostics()
    {
        await using var h = await FetchHarness.CreateAsync();

        var result = await h.FetchAsync("/article");

        Assert.Equal("Saltmere tram line opens", result.Title);
        Assert.Contains("Paragraph 1: the borough's new line", result.Markdown);
        Assert.Contains($"[timetable guide]({h.Site.Url("/guide/timetable")})", result.Markdown);
        Assert.DoesNotContain("Contact", result.Markdown);
        Assert.Equal(200, result.Diagnostics.TargetStatus);
        Assert.Equal(1, result.Diagnostics.Attempts);
        Assert.False(string.IsNullOrEmpty(result.Diagnostics.ExtractionStrategy));
        Assert.True(result.Diagnostics.QualityScore > 0.5);
        Assert.True(result.Diagnostics.SourceHtmlBytes > 0);
        Assert.False(result.Diagnostics.Cached);
        Assert.Equal(FetchRenderers.Browser, result.Diagnostics.Renderer);
        Assert.True(result.Diagnostics.NavigationMs > 0);
        Assert.True(result.Diagnostics.ReadinessMs > 0);
        Assert.True(result.Diagnostics.NavigationMs + result.Diagnostics.ReadinessMs <= result.Diagnostics.RenderMs);
    }

    [Fact]
    public async Task Content_rendered_by_script_is_waited_for()
    {
        await using var h = await FetchHarness.CreateAsync();

        var result = await h.FetchAsync("/spa");

        Assert.Contains("Client-side paragraph 7", result.Markdown);
        Assert.False(result.Diagnostics.ReadinessTimedOut);
    }

    [Fact]
    public async Task Content_from_a_code_chunk_that_arrives_late_is_waited_for()
    {
        await using var h = await FetchHarness.CreateAsync();

        var result = await h.FetchAsync("/spa-chunk");

        Assert.Contains("Chunk paragraph 7", result.Markdown);
        Assert.False(result.Diagnostics.ReadinessTimedOut);
    }

    [Fact]
    public async Task A_short_static_page_is_ready_once_its_quiet_period_ends_and_returns_its_text()
    {
        // The harness's quiet period is 200 ms: a short page must not wait for text that will never come.
        await using var h = await FetchHarness.CreateAsync();

        var result = await h.FetchAsync("/short");

        Assert.False(result.Diagnostics.ReadinessTimedOut);
        Assert.True(result.Diagnostics.ReadinessMs < 400, $"readiness took {result.Diagnostics.ReadinessMs} ms");
        Assert.Equal("whole-page", result.Diagnostics.ExtractionStrategy);
        Assert.True(result.Diagnostics.QualityScore <= 0.3);
        Assert.Contains("kept for use in examples", result.Markdown);
    }

    [Fact]
    public async Task A_page_that_only_toggles_classes_goes_quiet()
    {
        await using var h = await FetchHarness.CreateAsync();

        var result = await h.FetchAsync("/carousel");

        Assert.False(result.Diagnostics.ReadinessTimedOut);
        Assert.Contains("Paragraph 1", result.Markdown);
    }

    [Fact]
    public async Task The_ready_selector_is_awaited()
    {
        await using var h = await FetchHarness.CreateAsync();

        var result = await h.FetchAsync("/late", r => r with { ReadySelector = "#late .done" });

        Assert.Contains("Arrived late but awaited.", result.Markdown);
    }

    [Fact]
    public async Task A_ready_selector_that_never_appears_is_not_an_error()
    {
        await using var h = await FetchHarness.CreateAsync();

        var result = await h.FetchAsync("/article", r => r with { ReadySelector = "#never" });

        Assert.True(result.Diagnostics.ReadinessTimedOut);
        Assert.Contains("Paragraph 1", result.Markdown);
    }

    [Fact]
    public async Task Redirects_are_followed_and_the_final_url_reported()
    {
        await using var h = await FetchHarness.CreateAsync();

        var result = await h.FetchAsync("/redirect");

        Assert.Equal(h.Site.Url("/redirect"), result.RequestedUrl.AbsoluteUri);
        Assert.Equal(h.Site.Url("/article"), result.FinalUrl.AbsoluteUri);
    }

    [Fact]
    public async Task Popups_and_dialogs_cannot_hang_a_fetch()
    {
        await using var h = await FetchHarness.CreateAsync();

        var result = await h.FetchAsync("/popup");

        Assert.Contains("Page that opens things", result.Title);
    }

    [Fact]
    public async Task Media_is_not_downloaded_but_images_are()
    {
        await using var h = await FetchHarness.CreateAsync();

        await h.FetchAsync("/media");

        Assert.Equal(0, h.Site.Hits("/clip.mp4"));
        Assert.Equal(0, h.Site.Hits("/sound.mp3"));
        Assert.True(h.Site.Hits("/image.png") >= 1);
    }

    [Fact]
    public async Task The_minimal_policy_downloads_no_images_either()
    {
        await using var h = await FetchHarness.CreateAsync(new() { ["Resources:Policy"] = "Minimal" });

        var result = await h.FetchAsync("/media", r => r with { IncludeImages = true });

        Assert.Equal(0, h.Site.Hits("/image.png"));
        Assert.Equal(0, h.Site.Hits("/clip.mp4"));
        Assert.Contains("image.png", result.Markdown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("chromium")]
    public async Task Both_headless_modes_render(string channel)
    {
        // Empty is the headless shell (the default); "chromium" is new headless mode.
        await using var h = await FetchHarness.CreateAsync(new() { ["Browser:Channel"] = channel });

        var result = await h.FetchAsync("/article");

        Assert.Contains("Paragraph 1", result.Markdown);
    }

    [Fact]
    public async Task Hidden_elements_never_reach_the_markdown()
    {
        await using var h = await FetchHarness.CreateAsync();

        var result = await h.FetchAsync("/hidden");

        Assert.DoesNotContain("SECRET-HIDDEN-TEXT", result.Markdown);
    }
}
