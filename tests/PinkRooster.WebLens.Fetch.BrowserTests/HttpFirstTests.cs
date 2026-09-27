using System.Net;
using Microsoft.Extensions.Logging;

namespace PinkRooster.WebLens.Fetch.BrowserTests;

/// <summary>
/// The HTTP-first path against the fixture site: pages that need no browser are answered
/// without one; app shells, pages that ask for script, failures and challenges fall back to the browser; non-HTML and
/// refusals by the URL guard end the fetch. The proxy cases are in <see cref="HttpFirstProxyTests"/>.
/// </summary>
public class HttpFirstTests
{
    private static Task<FetchHarness> HttpFirst(Dictionary<string, string?>? more = null)
    {
        var settings = new Dictionary<string, string?> { ["HttpFirst:Enabled"] = "true" };
        foreach (var (key, value) in more ?? [])
        {
            settings[key] = value;
        }

        return FetchHarness.CreateAsync(settings);
    }

    [Fact]
    public async Task A_static_page_is_answered_without_the_browser()
    {
        await using var h = await HttpFirst();

        var result = await h.FetchAsync("/article");

        Assert.Equal(FetchRenderers.Http, result.Diagnostics.Renderer);
        Assert.Equal(1, result.Diagnostics.Attempts);
        Assert.Equal(0, result.Diagnostics.ReadinessMs);
        Assert.Contains("Paragraph 1: the borough's new line", result.Markdown);
        Assert.Null(h.Browsers.Current);
    }

    [Fact]
    public async Task A_short_page_is_answered_with_its_text_without_the_browser()
    {
        await using var h = await HttpFirst();

        var result = await h.FetchAsync("/short");

        Assert.Equal(FetchRenderers.Http, result.Diagnostics.Renderer);
        Assert.Equal("whole-page", result.Diagnostics.ExtractionStrategy);
        Assert.True(result.Diagnostics.QualityScore <= 0.3);
        Assert.Contains("kept for use in examples", result.Markdown);
    }

    [Fact]
    public async Task An_app_shell_is_rendered_in_the_browser()
    {
        await using var h = await HttpFirst();

        var result = await h.FetchAsync("/spa");

        Assert.Equal(FetchRenderers.Browser, result.Diagnostics.Renderer);
        Assert.Contains("Client-side paragraph 7", result.Markdown);
    }

    [Fact]
    public async Task A_page_whose_text_asks_for_javascript_is_rendered_in_the_browser()
    {
        await using var h = await HttpFirst();

        var result = await h.FetchAsync("/needs-js");

        Assert.Equal(FetchRenderers.Browser, result.Diagnostics.Renderer);
        Assert.Contains("Scripted story 5", result.Markdown);
        Assert.DoesNotContain("enable JavaScript", result.Markdown);
    }

    [Fact]
    public async Task A_challenge_to_the_plain_request_falls_back_to_the_browser_and_counts_for_nothing()
    {
        await using var h = await HttpFirst();

        var result = await h.FetchAsync("/challenge-plain");
        var again = await h.FetchAsync("/challenge-plain");

        Assert.Equal(FetchRenderers.Browser, result.Diagnostics.Renderer);
        Assert.Contains("Served to browsers", result.Title);
        Assert.Equal(FetchRenderers.Browser, again.Diagnostics.Renderer);
    }

    [Fact]
    public async Task A_plain_request_that_runs_out_of_time_falls_back_to_the_browser()
    {
        await using var h = await HttpFirst(new() { ["HttpFirst:Timeout"] = "00:00:00.300" });

        var result = await h.FetchAsync("/slow");

        Assert.Equal(FetchRenderers.Browser, result.Diagnostics.Renderer);
        Assert.Contains("Slow but fine", result.Title);
    }

    [Fact]
    public async Task A_response_that_is_not_html_is_refused_without_the_browser()
    {
        await using var h = await HttpFirst();

        var ex = await Assert.ThrowsAsync<FetchException>(() => h.FetchAsync("/pdf"));

        Assert.Equal(FetchErrorKind.UnsupportedContent, ex.Kind);
        Assert.Null(h.Browsers.Current);
    }

    [Fact]
    public async Task A_request_with_a_ready_selector_goes_straight_to_the_browser()
    {
        await using var h = await HttpFirst();

        var result = await h.FetchAsync("/late", r => r with { ReadySelector = "#late .done" });

        Assert.Equal(FetchRenderers.Browser, result.Diagnostics.Renderer);
        Assert.Equal(1, h.Site.Hits("/late"));
    }

    [Fact]
    public async Task A_redirect_to_a_denied_address_is_refused_and_never_reached()
    {
        var target = await FixtureSite.StartAsync("127.0.0.2");
        await using var _ = target;
        await using var h = await FetchHarness.CreateAsync(
            new() { ["HttpFirst:Enabled"] = "true", ["Security:DeniedRanges:0"] = "127.0.0.2/32" }, target.Port);

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            h.FetchAsync("/redirect-to?url=" + Uri.EscapeDataString(target.Url("/health/details"))));

        // Unlike the browser path, the plain request checks the hop before following it: the target is never contacted.
        Assert.Equal(FetchErrorKind.TargetNotAllowed, ex.Kind);
        Assert.Equal(0, target.TotalHits);
        Assert.Null(h.Browsers.Current);
    }
}

/// <summary>The HTTP-first path through the real egress proxy (as <see cref="EgressProxyTests"/> runs it).</summary>
public class HttpFirstProxyTests
{
    [Fact]
    public async Task The_plain_request_goes_through_the_proxy()
    {
        var (h, api, proxy) = await CreateAsync();
        await using var _ = h;
        await using var __ = api;
        await using var ___ = proxy;

        var result = await h.FetchAsync("/article");

        Assert.Equal(FetchRenderers.Http, result.Diagnostics.Renderer);
        Assert.Contains($"\"requested_host\":\"127.0.0.1:{h.Site.Port}\"", proxy.Logs());
    }

    [Fact]
    public async Task A_redirect_hop_to_localhost_is_stopped_by_the_proxy_and_ends_the_fetch()
    {
        var (h, api, proxy) = await CreateAsync();
        await using var _ = h;
        await using var __ = api;
        await using var ___ = proxy;

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            h.FetchAsync("/redirect-to?url=" + Uri.EscapeDataString($"http://127.0.0.1:{api.Port}/health/details")));

        Assert.Equal(FetchErrorKind.TargetUnavailable, ex.Kind);
        Assert.True(api.TotalHits == 0, $"the redirect hop reached the target: {api.Summary()}");
        Assert.True(h.Logs.Has(LogLevel.Warning, "egress proxy refused"), "the proxy denial was not logged at Warning");
        Assert.Null(h.Browsers.Current);
    }

    [Fact]
    public async Task Https_to_localhost_is_refused_at_the_connect_without_the_browser()
    {
        var (h, api, proxy) = await CreateAsync();
        await using var _ = h;
        await using var __ = api;
        await using var ___ = proxy;

        var ex = await Assert.ThrowsAsync<FetchException>(() => h.FetchAsync($"https://127.0.0.1:{api.Port}/health/details"));

        Assert.Equal(FetchErrorKind.TargetUnavailable, ex.Kind);
        Assert.Equal(0, api.TotalHits);
        Assert.True(h.Logs.Has(LogLevel.Warning, "egress proxy refused"));
        Assert.Null(h.Browsers.Current);
    }

    private static async Task<(FetchHarness Harness, FixtureSite Api, EgressProxy Proxy)> CreateAsync()
    {
        EgressProxy.RequireDocker();
        var api = await FixtureSite.StartAsync();
        var site = await FixtureSite.StartAsync();
        var proxy = await EgressProxy.StartAsync(new IPEndPoint(IPAddress.Loopback, site.Port));
        var h = await FetchHarness.CreateForSiteAsync(
            site,
            new() { ["Security:EgressProxy:Server"] = proxy.Server, ["Security:RequireEgressProxy"] = "true", ["HttpFirst:Enabled"] = "true" },
            api.Port);
        return (h, api, proxy);
    }
}
