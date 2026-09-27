namespace PinkRooster.WebLens.Fetch.BrowserTests;

/// <summary>Fetch caching in the real browser with the L1 cache on, and HybridCache's shared execution: one factory run under the combined cancellation of all waiting callers.</summary>
public class CacheTests
{
    private static Task<FetchHarness> Cached() => FetchHarness.CreateAsync(new() { ["Cache:Enabled"] = "true" });

    [Fact]
    public async Task A_repeated_fetch_is_served_from_the_cache_without_the_browser()
    {
        await using var h = await Cached();

        var first = await h.FetchAsync("/article");
        var second = await h.FetchAsync("/article#another-fragment");

        Assert.Equal(1, h.Site.Hits("/article"));
        Assert.False(first.Diagnostics.Cached);
        Assert.True(second.Diagnostics.Cached);
        Assert.Equal(first.Markdown, second.Markdown);
    }

    [Fact]
    public async Task A_lookup_finds_only_what_a_fetch_stored_and_never_opens_the_page()
    {
        await using var h = await Cached();
        var request = new FetchRequest(h.Site.Url("/article"));
        var ct = TestContext.Current.CancellationToken;

        var before = await h.Fetch.TryGetCachedAsync(request, ct);
        await h.FetchAsync("/article");
        var hit = await h.Fetch.TryGetCachedAsync(request, ct);
        var bypassed = await h.Fetch.TryGetCachedAsync(request with { BypassCache = true }, ct);

        Assert.Null(before);
        Assert.Equal(1, h.Site.Hits("/article"));
        Assert.NotNull(hit);
        Assert.True(hit.Diagnostics.Cached);
        Assert.Null(bypassed);
    }

    [Fact]
    public async Task Output_changing_options_are_separate_entries_and_the_timeout_is_not()
    {
        await using var h = await Cached();

        await h.FetchAsync("/article");
        await h.FetchAsync("/article", r => r with { IncludeLinks = false });
        var withTimeout = await h.FetchAsync("/article", r => r with { Timeout = TimeSpan.FromSeconds(20) });

        Assert.Equal(2, h.Site.Hits("/article"));
        Assert.True(withTimeout.Diagnostics.Cached);
    }

    [Fact]
    public async Task Bypass_skips_the_read_and_refreshes_the_entry()
    {
        await using var h = await Cached();

        await h.FetchAsync("/article");
        var bypassed = await h.FetchAsync("/article", r => r with { BypassCache = true });
        var after = await h.FetchAsync("/article");

        Assert.Equal(2, h.Site.Hits("/article"));
        Assert.False(bypassed.Diagnostics.Cached);
        Assert.True(after.Diagnostics.Cached);
    }

    [Fact]
    public async Task Failures_and_results_whose_readiness_timed_out_are_never_stored()
    {
        await using var h = await Cached();

        await Assert.ThrowsAsync<FetchException>(() => h.FetchAsync("/status/404"));
        await Assert.ThrowsAsync<FetchException>(() => h.FetchAsync("/status/404"));
        var incomplete = await h.FetchAsync("/article", r => r with { ReadySelector = "#never" });
        var again = await h.FetchAsync("/article", r => r with { ReadySelector = "#never" });

        Assert.Equal(2, h.Site.Hits("/status/404"));
        Assert.True(incomplete.Diagnostics.ReadinessTimedOut);
        Assert.False(again.Diagnostics.Cached);
        Assert.Equal(2, h.Site.Hits("/article"));
    }

    [Fact]
    public async Task A_page_that_adds_content_forever_times_out_and_is_not_stored()
    {
        await using var h = await Cached();

        var first = await h.FetchAsync("/ticker");
        var second = await h.FetchAsync("/ticker");

        Assert.True(first.Diagnostics.ReadinessTimedOut);
        Assert.False(second.Diagnostics.Cached);
        Assert.Equal(2, h.Site.Hits("/ticker"));
    }

    [Fact]
    public async Task When_the_first_of_several_coalesced_callers_cancels_the_others_still_get_the_result()
    {
        await using var h = await Cached();
        using var firstCaller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var first = h.FetchAsync("/slow", ct: firstCaller.Token);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var second = h.FetchAsync("/slow");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await firstCaller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var result = await second;

        Assert.Contains("Slow but fine", result.Title);
        Assert.Equal(1, h.Site.Hits("/slow"));
    }

    [Fact]
    public async Task A_callers_short_timeout_ends_only_its_own_wait_and_the_shared_result_is_still_cached()
    {
        await using var h = await Cached();

        var patient = h.FetchAsync("/slow");
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var hasty = await Assert.ThrowsAsync<FetchException>(() => h.FetchAsync("/slow", r => r with { Timeout = TimeSpan.FromMilliseconds(700) }));
        var result = await patient;
        var later = await h.FetchAsync("/slow");

        Assert.Equal(FetchErrorKind.Timeout, hasty.Kind);
        Assert.Contains("Slow but fine", result.Title);
        Assert.True(later.Diagnostics.Cached);
        Assert.Equal(1, h.Site.Hits("/slow"));
    }
}
