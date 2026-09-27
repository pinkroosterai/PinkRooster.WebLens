using System.Net;
using Microsoft.Extensions.Logging;

namespace PinkRooster.WebLens.Fetch.BrowserTests;

/// <summary>
/// SSRF layers L1 (URL guard) and L2 (route guard) in the real browser, without the proxy. The fixture site is on 127.0.0.1; a second site on
/// 127.0.0.2 stands for a private target, refused through a configured denied range (the built-in ranges are off,
/// because the fixture itself is on loopback). What L2 cannot stop, redirect hops, is the proxy's: see EgressProxyTests.
/// </summary>
public class SsrfTests
{
    private static async Task<(FetchHarness Harness, FixtureSite Private)> CreateAsync()
    {
        var target = await FixtureSite.StartAsync("127.0.0.2");
        var harness = await FetchHarness.CreateAsync(new() { ["Security:DeniedRanges:0"] = "127.0.0.2/32" }, target.Port);
        return (harness, target);
    }

    [Fact]
    public async Task A_private_target_named_directly_is_refused_before_any_browser_work()
    {
        var (h, target) = await CreateAsync();
        await using var _ = h;
        await using var __ = target;

        var ex = await Assert.ThrowsAsync<FetchException>(() => h.FetchAsync(target.Url("/health/details")));

        Assert.Equal(FetchErrorKind.TargetNotAllowed, ex.Kind);
        Assert.Equal(0, target.TotalHits);
        Assert.Null(h.Browsers.Current);
    }

    [Fact]
    public async Task Subresources_frames_fetches_and_websockets_to_a_private_target_never_leave_the_browser()
    {
        var (h, target) = await CreateAsync();
        await using var _ = h;
        await using var __ = target;

        var result = await h.FetchAsync("/reaches-out?url=" + Uri.EscapeDataString(target.BaseUri.AbsoluteUri.TrimEnd('/')));

        Assert.Contains("Page that reaches out", result.Title);
        Assert.True(target.TotalHits == 0, $"the private target was reached: {target.Summary()}");
        Assert.True(h.Logs.Has(LogLevel.Information, "route guard refused"), "the route guard's refusals were not logged");
    }

    [Fact]
    public async Task A_redirect_to_a_private_target_returns_nothing_from_it()
    {
        var (h, target) = await CreateAsync();
        await using var _ = h;
        await using var __ = target;

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            h.FetchAsync("/redirect-to?url=" + Uri.EscapeDataString(target.Url("/health/details"))));

        // Playwright's route interception never shows the guard a redirect hop, so without the proxy the request is made;
        // the chain check refuses its content. Stopping the request itself is the proxy's job (EgressProxyTests).
        Assert.Equal(FetchErrorKind.TargetNotAllowed, ex.Kind);
        Assert.DoesNotContain("SECRET-HEALTH-DETAILS", ex.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:22/")]
    [InlineData("http://user:secret@127.0.0.1/")]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    public async Task Ports_credentials_and_other_schemes_are_refused(string url)
    {
        await using var h = await FetchHarness.CreateAsync();

        var ex = await Assert.ThrowsAsync<FetchException>(() => h.FetchAsync(url));

        Assert.True(ex.Kind is FetchErrorKind.TargetNotAllowed or FetchErrorKind.InvalidRequest, ex.Kind.ToString());
    }

    [Fact]
    public async Task The_browser_does_not_inherit_the_hosts_secrets()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Reads /proc; Linux only.");
        }

        Environment.SetEnvironmentVariable("WEBLENS_TEST_SECRET", "hunter2-marker");
        try
        {
            await using var h = await FetchHarness.CreateAsync();
            await h.FetchAsync("/article");

            var browsers = BrowserProcesses().ToList();
            Assert.NotEmpty(browsers);
            Assert.All(browsers, environ => Assert.DoesNotContain("hunter2-marker", environ));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBLENS_TEST_SECRET", null);
        }
    }

    /// <summary>The environments of Chromium processes started by this test process's Playwright driver.</summary>
    private static IEnumerable<string> BrowserProcesses()
    {
        var mine = Environment.ProcessId;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            string cmdline, environ;
            try
            {
                cmdline = File.ReadAllText(Path.Combine(dir, "cmdline"));
                environ = File.ReadAllText(Path.Combine(dir, "environ"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (cmdline.Contains("ms-playwright", StringComparison.Ordinal) && cmdline.Contains("chrome", StringComparison.Ordinal)
                && DescendsFrom(Path.GetFileName(dir), mine))
            {
                yield return environ;
            }
        }
    }

    private static bool DescendsFrom(string pid, int ancestor)
    {
        for (var i = 0; i < 10 && int.TryParse(pid, out var current) && current > 1; i++)
        {
            if (current == ancestor)
            {
                return true;
            }

            try
            {
                var stat = File.ReadAllText($"/proc/{current}/stat");
                pid = stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1];
            }
            catch (IOException)
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>
/// SSRF layer L3: the real egress proxy (deploy/egress-proxy) as the boundary. L1 and L2 are relaxed here (private
/// networks allowed, every test port allowed) so that only the proxy stands between the browser and the target.
/// </summary>
public class EgressProxyTests
{
    [Fact]
    public async Task The_allowed_fixture_is_fetched_through_the_proxy()
    {
        var (h, api, proxy) = await CreateWithAllowedFixtureAsync();
        await using var _ = h;
        await using var __ = api;
        await using var ___ = proxy;

        var result = await h.FetchAsync("/article");

        Assert.Contains("Saltmere", result.Title);
        Assert.Contains($"\"requested_host\":\"127.0.0.1:{h.Site.Port}\"", proxy.Logs());
    }

    [Fact]
    public async Task Localhost_health_details_are_refused_by_the_proxy_and_never_reached()
    {
        var (h, api, proxy) = await CreateWithAllowedFixtureAsync();
        await using var _ = h;
        await using var __ = api;
        await using var ___ = proxy;

        var ex = await Assert.ThrowsAsync<FetchException>(() => h.FetchAsync($"http://localhost:{api.Port}/health/details"));

        Assert.Equal(FetchErrorKind.TargetUnavailable, ex.Kind);
        Assert.Equal(0, api.TotalHits);
        Assert.True(h.Logs.Has(LogLevel.Warning, "egress proxy refused"), "the proxy denial was not logged at Warning");
    }

    [Fact]
    public async Task Https_to_localhost_is_refused_at_the_connect()
    {
        var (h, api, proxy) = await CreateWithAllowedFixtureAsync();
        await using var _ = h;
        await using var __ = api;
        await using var ___ = proxy;

        var ex = await Assert.ThrowsAsync<FetchException>(() => h.FetchAsync($"https://127.0.0.1:{api.Port}/health/details"));

        Assert.Equal(FetchErrorKind.TargetUnavailable, ex.Kind);
        Assert.Equal(0, api.TotalHits);
        Assert.True(h.Logs.Has(LogLevel.Warning, "egress proxy refused"));
    }

    [Fact]
    public async Task An_address_on_the_docker_network_where_searxng_lives_is_refused()
    {
        var (h, api, proxy) = await CreateWithAllowedFixtureAsync();
        await using var _ = h;
        await using var __ = api;
        await using var ___ = proxy;
        var neighbour = EgressProxy.BridgeNeighbour();

        var ex = await Assert.ThrowsAsync<FetchException>(() => h.FetchAsync($"http://{neighbour}:8080/"));

        Assert.Equal(FetchErrorKind.TargetUnavailable, ex.Kind);
        Assert.Contains($"\"requested_host\":\"{neighbour}:8080\"", proxy.Logs());
        Assert.Contains("Deny: Private Range", proxy.Logs());
    }

    [Fact]
    public async Task A_redirect_hop_to_localhost_is_stopped_by_the_proxy()
    {
        var (h, api, proxy) = await CreateWithAllowedFixtureAsync();
        await using var _ = h;
        await using var __ = api;
        await using var ___ = proxy;

        await Assert.ThrowsAsync<FetchException>(() =>
            h.FetchAsync("/redirect-to?url=" + Uri.EscapeDataString($"http://127.0.0.1:{api.Port}/health/details")));

        Assert.True(api.TotalHits == 0, $"the redirect hop reached the target: {api.Summary()}");
    }

    [Fact]
    public async Task Page_requests_and_websockets_to_localhost_are_stopped_by_the_proxy()
    {
        var (h, api, proxy) = await CreateWithAllowedFixtureAsync();
        await using var _ = h;
        await using var __ = api;
        await using var ___ = proxy;

        var result = await h.FetchAsync("/reaches-out?url=" + Uri.EscapeDataString($"http://127.0.0.1:{api.Port}"));

        Assert.Contains("Page that reaches out", result.Title);
        Assert.True(api.TotalHits == 0, $"a page request reached the target: {api.Summary()}");
    }

    /// <summary>The harness's fixture is allowed through the proxy; everything else keeps the proxy's default deny list.</summary>
    private static async Task<(FetchHarness Harness, FixtureSite Api, EgressProxy Proxy)> CreateWithAllowedFixtureAsync()
    {
        EgressProxy.RequireDocker();

        // Stands in for this service's own API on localhost: /health/details answers with a marker.
        var api = await FixtureSite.StartAsync();

        // The proxy must know the fixture's port before the harness points at the proxy, so start the site first.
        var site = await FixtureSite.StartAsync();
        var proxy = await EgressProxy.StartAsync(new IPEndPoint(IPAddress.Loopback, site.Port));
        var h = await FetchHarness.CreateForSiteAsync(
            site,
            new() { ["Security:EgressProxy:Server"] = proxy.Server, ["Security:RequireEgressProxy"] = "true" },
            api.Port,
            8080);
        return (h, api, proxy);
    }
}
