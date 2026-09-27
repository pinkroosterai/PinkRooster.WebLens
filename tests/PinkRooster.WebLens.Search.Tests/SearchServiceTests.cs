using System.Net;
using System.Text;

namespace PinkRooster.WebLens.Search.Tests;

public class SearchServiceTests
{
    private static readonly string Ok = Fixture.Read(Fixture.Search);

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static Task<HttpResponseMessage> Fail(HttpRequestError error) =>
        throw new HttpRequestException(error, "connection failed");

    /// <summary>/config from a SearXNG that offers only the engine <c>onlyhere</c> and the category <c>general</c>.</summary>
    private const string NarrowConfig = """{ "version": "2026.9.22", "categories": ["general"], "engines": [{ "name": "onlyhere", "shortcut": "oh", "enabled": true }] }""";

    private static FakeSearxServer.Handler NarrowSearxng(string search) => (request, _) =>
        Task.FromResult(FakeSearxServer.Json(request.RequestUri!.AbsolutePath == "/config" ? NarrowConfig : search));

    /// <summary>A 200 whose body breaks off: what SocketsHttpHandler reports as HttpIOException when the connection drops mid-body.</summary>
    private static HttpResponseMessage BrokenBody() => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(new BrokenStream()) { Headers = { { "Content-Type", "application/json" } } },
    };

    private static void OpenCircuit(SearchHarness h, string instance)
    {
        for (var i = 0; i < 3; i++)
        {
            h.Health.ReportTransportFailure(instance);
        }
    }

    // ---- happy path -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Returns_mapped_results_and_posts_a_form_with_json_forced()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok);
        using var h = SearchHarness.Create(1, server: server);

        var response = await h.Search.SearchAsync(new SearchQuery("cats") { Engines = ["brave"], Language = "en" }, TestContext.Current.CancellationToken);

        Assert.NotEmpty(response.Results);
        var request = Assert.Single(server.SearchRequestsTo("a.test"));
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("format=json", request.Body);
        Assert.Contains("q=%21brave+cats", request.Body);
        Assert.Contains("language=en", request.Body);
        Assert.DoesNotContain("theme", request.Body);
        Assert.Equal(InstanceStateKind.Healthy, h.Health.Get("a").Kind);
    }

    [Fact]
    public async Task Get_is_available_for_diagnostics()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok);
        using var h = SearchHarness.Create(1, new() { ["Transport:Method"] = "Get" }, server);

        await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, server.SearchRequestsTo("a.test").Single().Method);
    }

    [Fact]
    public async Task The_limit_is_applied_last_and_reflected_in_the_result_count()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok);
        using var h = SearchHarness.Create(1, server: server);

        var response = await h.Search.SearchAsync(new SearchQuery("cats") { Limit = 3 }, TestContext.Current.CancellationToken);

        Assert.Equal(3, response.Results.Count);
        Assert.Equal(3, response.Meta.ResultCount);
    }

    [Fact]
    public async Task Unresponsive_engines_are_a_partial_success_and_not_an_instance_failure()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Fixture.Read(Fixture.Unresponsive));
        using var h = SearchHarness.Create(1, server: server);

        var response = await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.True(response.Meta.Partial);
        Assert.Equal(InstanceStateKind.Healthy, h.Health.Get("a").Kind);
    }

    [Fact]
    public async Task Zero_results_is_a_healthy_empty_success()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", """{ "query": "x", "results": [], "unresponsive_engines": [] }""");
        using var h = SearchHarness.Create(1, server: server);

        var response = await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.Empty(response.Results);
        Assert.Equal(InstanceStateKind.Healthy, h.Health.Get("a").Kind);
    }

    // ---- failover ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Fails_over_to_the_next_instance_and_degrades_the_failed_one()
    {
        var server = new FakeSearxServer()
            .OnSearxng("a.test", _ => FakeSearxServer.Status(HttpStatusCode.InternalServerError))
            .OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, server: server);

        var response = await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.NotEmpty(response.Results);
        Assert.Single(server.SearchRequestsTo("a.test"));
        Assert.Single(server.SearchRequestsTo("b.test"));
        Assert.Equal(InstanceStateKind.Degraded, h.Health.Get("a").Kind);
        Assert.Equal(InstanceStateKind.Healthy, h.Health.Get("b").Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task Server_errors_fail_over_without_retrying_the_same_instance(HttpStatusCode status)
    {
        var server = new FakeSearxServer()
            .OnSearxng("a.test", _ => FakeSearxServer.Status(status))
            .OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, server: server);

        await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.Single(server.SearchRequestsTo("a.test"));
    }

    [Fact]
    public async Task A_connection_failure_is_retried_once_on_the_same_instance_then_fails_over()
    {
        var server = new FakeSearxServer()
            .On("a.test", (_, _) => Fail(HttpRequestError.ConnectionError))
            .OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, server: server);

        await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.Equal(2, server.SearchRequestsTo("a.test").Count);
        Assert.Single(server.SearchRequestsTo("b.test"));
    }

    [Fact]
    public async Task A_connection_failure_that_recovers_on_the_retry_stays_on_the_instance()
    {
        var searches = 0;
        var server = new FakeSearxServer().On("a.test", (request, _) =>
        {
            // The background /config fetch shares this handler; only /search calls are counted.
            if (request.RequestUri!.AbsolutePath == "/config")
            {
                return Task.FromResult(FakeSearxServer.Status(HttpStatusCode.NotFound));
            }

            return Interlocked.Increment(ref searches) == 1 ? Fail(HttpRequestError.NameResolutionError) : Task.FromResult(FakeSearxServer.Json(Ok));
        });
        using var h = SearchHarness.Create(1, server: server);

        var response = await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.NotEmpty(response.Results);
        Assert.Equal(2, server.SearchRequestsTo("a.test").Count);
    }

    [Fact]
    public async Task Other_network_errors_are_not_retried_on_the_same_instance()
    {
        var server = new FakeSearxServer()
            .On("a.test", (_, _) => Fail(HttpRequestError.ResponseEnded))
            .OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, server: server);

        await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.Single(server.SearchRequestsTo("a.test"));
    }

    [Fact]
    public async Task The_total_attempt_cap_bounds_upstream_load_whatever_the_pool_size()
    {
        var server = new FakeSearxServer();
        foreach (var host in new[] { "a.test", "b.test", "c.test" })
        {
            server.OnSearxng(host, _ => FakeSearxServer.Status(HttpStatusCode.InternalServerError));
        }

        using var h = SearchHarness.Create(3, new() { ["Routing:MaxTotalAttempts"] = "2" }, server);

        var ex = await Assert.ThrowsAsync<SearchException>(() => h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken));

        Assert.Equal(SearchErrorKind.AllAttemptsFailed, ex.Kind);
        Assert.Equal(2, server.Requests.Count(r => r.Path == "/search"));
        Assert.Equal(2, ex.Attempts.Count);
    }

    [Fact]
    public async Task The_instance_attempt_cap_limits_how_many_instances_are_tried()
    {
        var server = new FakeSearxServer();
        foreach (var host in new[] { "a.test", "b.test", "c.test" })
        {
            server.OnSearxng(host, _ => FakeSearxServer.Status(HttpStatusCode.InternalServerError));
        }

        using var h = SearchHarness.Create(3, new() { ["Routing:MaxInstanceAttempts"] = "1" }, server);

        await Assert.ThrowsAsync<SearchException>(() => h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken));

        Assert.Single(server.Requests, r => r.Path == "/search");
    }

    [Fact]
    public async Task Repeated_failures_open_the_circuit_and_later_searches_skip_the_instance_without_a_network_call()
    {
        var server = new FakeSearxServer()
            .OnSearxng("a.test", _ => FakeSearxServer.Status(HttpStatusCode.InternalServerError))
            .OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, new() { ["Routing:CircuitFailureThreshold"] = "2" }, server);
        var ct = TestContext.Current.CancellationToken;

        await h.Search.SearchAsync(SearchHarness.Query(), ct);
        await h.Search.SearchAsync(SearchHarness.Query(), ct);
        Assert.Equal(InstanceStateKind.OpenCircuit, h.Health.Get("a").Kind);

        await h.Search.SearchAsync(SearchHarness.Query(), ct);

        Assert.Equal(2, server.SearchRequestsTo("a.test").Count);
        Assert.Equal(3, server.SearchRequestsTo("b.test").Count);
    }

    [Fact]
    public async Task After_the_break_duration_one_probe_reaches_the_instance_and_closes_the_circuit()
    {
        var healthy = false;
        var server = new FakeSearxServer()
            .OnSearxng("a.test", _ => healthy ? FakeSearxServer.Json(Ok) : FakeSearxServer.Status(HttpStatusCode.InternalServerError));
        using var h = SearchHarness.Create(1, new() { ["Routing:CircuitFailureThreshold"] = "1" }, server);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<SearchException>(() => h.Search.SearchAsync(SearchHarness.Query(), ct));
        Assert.Equal(InstanceStateKind.OpenCircuit, h.Health.Get("a").Kind);

        var noneEligible = await Assert.ThrowsAsync<SearchException>(() => h.Search.SearchAsync(SearchHarness.Query(), ct));
        Assert.Equal(SearchErrorKind.NoEligibleInstance, noneEligible.Kind);
        Assert.Equal(TimeSpan.FromSeconds(30), noneEligible.RetryAfter);
        Assert.Single(server.SearchRequestsTo("a.test"));

        healthy = true;
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Search.SearchAsync(SearchHarness.Query(), ct);

        Assert.Equal(InstanceStateKind.Healthy, h.Health.Get("a").Kind);
    }

    // ---- outcome classification ----------------------------------------------------------------------------

    [Fact]
    public async Task Too_many_requests_puts_the_instance_on_cooldown_and_fails_over_immediately()
    {
        var server = new FakeSearxServer()
            .OnSearxng("a.test", _ =>
            {
                var response = FakeSearxServer.Status(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
                return response;
            })
            .OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, server: server);

        await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.Equal(InstanceStateKind.RateLimited, h.Health.Get("a").Kind);
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromSeconds(90), h.Health.Get("a").Until);
    }

    [Fact]
    public async Task When_every_instance_is_rate_limited_nothing_is_eligible_and_the_recovery_time_is_reported()
    {
        var server = new FakeSearxServer().OnSearxng("a.test", _ => FakeSearxServer.Status(HttpStatusCode.TooManyRequests));
        using var h = SearchHarness.Create(1, server: server);

        var ex = await Assert.ThrowsAsync<SearchException>(() => h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken));

        Assert.Equal(SearchErrorKind.NoEligibleInstance, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
    }

    [Fact]
    public async Task A_403_from_a_known_searxng_means_json_is_disabled()
    {
        var server = new FakeSearxServer()
            .OnSearxng("a.test", _ => FakeSearxServer.Status(HttpStatusCode.Forbidden))
            .OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, server: server);

        await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.Equal(InstanceStateKind.JsonUnsupported, h.Health.Get("a").Kind);
    }

    [Fact]
    public async Task A_403_from_something_that_is_not_searxng_is_generic_access_denied()
    {
        // No SearXNG /config: a reverse proxy or WAF answering 403 must not be mistaken for "JSON disabled".
        var server = new FakeSearxServer()
            .On("a.test", (_, _) => Task.FromResult(FakeSearxServer.Status(HttpStatusCode.Forbidden)))
            .OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, server: server);

        await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.Equal(InstanceStateKind.AccessDenied, h.Health.Get("a").Kind);
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromMinutes(1), h.Health.Get("a").Until);
    }

    [Fact]
    public async Task A_401_is_access_denied()
    {
        var server = new FakeSearxServer()
            .OnSearxng("a.test", _ => FakeSearxServer.Status(HttpStatusCode.Unauthorized))
            .OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, server: server);

        await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.Equal(InstanceStateKind.AccessDenied, h.Health.Get("a").Kind);
    }

    public static TheoryData<string> ProtocolFailures => new()
    {
        "404", "405", "html", "invalid-json", "oversize-known-length", "oversize-unknown-length", "json-null",
    };

    [Theory]
    [MemberData(nameof(ProtocolFailures))]
    public async Task Protocol_failures_mark_the_instance_incompatible_and_fail_over(string kind)
    {
        HttpResponseMessage Respond(HttpRequestMessage _) => kind switch
        {
            "404" => FakeSearxServer.Status(HttpStatusCode.NotFound),
            "405" => FakeSearxServer.Status(HttpStatusCode.MethodNotAllowed),
            "html" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>", Encoding.UTF8, "text/html") },
            "invalid-json" => FakeSearxServer.Json("{ not json"),
            "oversize-known-length" => FakeSearxServer.Json(new string(' ', 150_000) + "{}"),
            "oversize-unknown-length" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(new string(' ', 150_000) + "{}")) { Headers = { { "Content-Type", "application/json" } } } },
            "json-null" => FakeSearxServer.Json("null"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var server = new FakeSearxServer().OnSearxng("a.test", Respond).OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, new() { ["Transport:MaxResponseBytes"] = "100000" }, server);

        await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.Equal(InstanceStateKind.ProtocolIncompatible, h.Health.Get("a").Kind);
    }

    [Fact]
    public async Task A_400_is_an_invalid_query_and_does_not_fail_over()
    {
        var server = new FakeSearxServer()
            .OnSearxng("a.test", _ => FakeSearxServer.Status(HttpStatusCode.BadRequest))
            .OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, server: server);

        var ex = await Assert.ThrowsAsync<SearchException>(() => h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken));

        Assert.Equal(SearchErrorKind.InvalidQuery, ex.Kind);
        Assert.Empty(server.SearchRequestsTo("b.test"));
        Assert.NotEqual(InstanceStateKind.Degraded, h.Health.Get("a").Kind);
    }

    [Theory]
    [InlineData("http://duckduckgo.com/?q=cats")]
    [InlineData("https://www.bing.com/search?q=cats")]
    [InlineData("http://wordpress.org/search/cats")]
    public async Task A_redirect_to_a_different_host_is_an_external_bang_and_is_unsupported(string location)
    {
        var server = new FakeSearxServer()
            .OnSearxng("a.test", _ => Redirect(location))
            .OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, server: server);

        var ex = await Assert.ThrowsAsync<SearchException>(() => h.Search.SearchAsync(new SearchQuery("!!ddg cats"), TestContext.Current.CancellationToken));

        Assert.Equal(SearchErrorKind.QueryUnsupported, ex.Kind);
        Assert.Empty(server.SearchRequestsTo("b.test"));
        Assert.Equal(InstanceStateKind.Unknown, h.Health.Get("a").Kind);
    }

    [Theory]
    [InlineData("https://a.test/search")]
    [InlineData("/search/")]
    [InlineData("http://a.test/search")]
    [InlineData("https://a.test:8443/search")]
    public async Task A_redirect_to_the_same_host_is_a_misconfigured_base_uri_and_fails_over(string location)
    {
        var server = new FakeSearxServer()
            .OnSearxng("a.test", _ => Redirect(location))
            .OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, server: server);

        await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.Equal(InstanceStateKind.ProtocolIncompatible, h.Health.Get("a").Kind);
    }

    [Fact]
    public async Task Redirects_are_never_followed()
    {
        var server = new FakeSearxServer().OnSearxng("a.test", _ => Redirect("https://elsewhere.test/x"));
        using var h = SearchHarness.Create(1, server: server);

        await Assert.ThrowsAsync<SearchException>(() => h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain(server.Requests, r => r.Host == "elsewhere.test");
    }

    [Fact]
    public async Task A_connection_lost_mid_body_is_a_transport_failure_and_fails_over()
    {
        var server = new FakeSearxServer().OnSearxng("a.test", _ => BrokenBody()).OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, server: server);

        var response = await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.NotEmpty(response.Results);
        Assert.Equal(InstanceStateKind.Degraded, h.Health.Get("a").Kind);
    }

    [Fact]
    public async Task A_probe_that_ends_in_an_unexpected_exception_returns_its_permit()
    {
        var broken = true;
        var server = new FakeSearxServer().OnSearxng("a.test", _ => broken ? throw new InvalidOperationException("bug") : FakeSearxServer.Json(Ok));
        using var h = SearchHarness.Create(1, server: server);
        OpenCircuit(h, "a");
        h.Time.Advance(TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken));

        Assert.True(h.Health.IsEligible("a"));
        broken = false;
        await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);
        Assert.Equal(InstanceStateKind.Healthy, h.Health.Get("a").Kind);
    }

    [Fact]
    public async Task A_403_whose_config_hangs_is_classified_within_one_attempt_and_fails_over()
    {
        var server = new FakeSearxServer()
            .On("a.test", async (request, ct) =>
            {
                if (request.RequestUri!.AbsolutePath == "/config")
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }

                return FakeSearxServer.Status(HttpStatusCode.Forbidden);
            })
            .OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(
            2,
            new() { ["Timeouts:Attempt"] = "00:00:00.150", ["Timeouts:Total"] = "00:00:05" },
            server,
            clock: TimeProvider.System);

        var response = await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.NotEmpty(response.Results);
        Assert.Equal(InstanceStateKind.AccessDenied, h.Health.Get("a").Kind);
    }

    // ---- budgets and cancellation ----------------------------------------------------------------------------

    [Fact]
    public async Task A_slow_attempt_times_out_and_fails_over()
    {
        var server = new FakeSearxServer()
            .On("a.test", async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return FakeSearxServer.Status(HttpStatusCode.OK);
            })
            .OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(
            2,
            new() { ["Timeouts:Attempt"] = "00:00:00.150", ["Timeouts:Total"] = "00:00:05" },
            server,
            clock: TimeProvider.System);

        var response = await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.NotEmpty(response.Results);
        Assert.Equal(InstanceStateKind.Degraded, h.Health.Get("a").Kind);
    }

    [Fact]
    public async Task Running_out_of_total_budget_is_a_timeout_not_a_cancellation()
    {
        async Task<HttpResponseMessage> Slow(HttpRequestMessage _, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return FakeSearxServer.Status(HttpStatusCode.OK);
        }

        // Each attempt times out at 250 ms and fails over; the second one is still running when the 300 ms budget ends.
        var server = new FakeSearxServer().On("a.test", Slow).On("b.test", Slow);
        using var h = SearchHarness.Create(
            2,
            new() { ["Timeouts:Attempt"] = "00:00:00.250", ["Timeouts:Total"] = "00:00:00.300" },
            server,
            clock: TimeProvider.System);

        var ex = await Assert.ThrowsAsync<SearchException>(() => h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken));

        Assert.Equal(SearchErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_as_a_cancellation_and_leaves_health_alone()
    {
        using var cts = new CancellationTokenSource();
        var server = new FakeSearxServer().On("a.test", async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return FakeSearxServer.Status(HttpStatusCode.OK);
        });
        using var h = SearchHarness.Create(1, server: server, clock: TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Search.SearchAsync(SearchHarness.Query(), cts.Token));

        Assert.Equal(InstanceStateKind.Unknown, h.Health.Get("a").Kind);
    }

    // ---- selection --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Health_aware_round_robin_alternates_between_healthy_instances_in_a_tier()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok).OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(
            2,
            new()
            {
                ["Routing:Policy"] = "HealthAwareRoundRobin",
                ["Instances:1:Priority"] = "0",
            },
            server);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 4; i++)
        {
            await h.Search.SearchAsync(SearchHarness.Query(), ct);
        }

        Assert.Equal(2, server.SearchRequestsTo("a.test").Count);
        Assert.Equal(2, server.SearchRequestsTo("b.test").Count);
    }

    [Fact]
    public async Task A_lower_priority_number_is_preferred_and_a_higher_tier_is_only_a_fallback()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok).OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, new() { ["Routing:Policy"] = "HealthAwareRoundRobin" }, server);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 3; i++)
        {
            await h.Search.SearchAsync(SearchHarness.Query(), ct);
        }

        Assert.Equal(3, server.SearchRequestsTo("a.test").Count);
        Assert.Empty(server.SearchRequestsTo("b.test"));
    }

    [Fact]
    public async Task Degraded_instances_sort_after_healthy_ones_in_the_same_tier()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok).OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, new() { ["Routing:Policy"] = "HealthAwareRoundRobin", ["Instances:1:Priority"] = "0" }, server);
        h.Health.ReportTransportFailure("a");

        for (var i = 0; i < 3; i++)
        {
            await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);
        }

        Assert.Empty(server.SearchRequestsTo("a.test"));
        Assert.Equal(3, server.SearchRequestsTo("b.test").Count);
    }

    // ---- capabilities -----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_engine_the_fresh_snapshot_says_is_missing_skips_the_instance()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok).OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, server: server);
        var instance = h.Selector.Instances.Single(i => i.Name == "a");
        await h.Capabilities.ProbeAsync(instance, TestContext.Current.CancellationToken);
        await h.Capabilities.ProbeAsync(h.Selector.Instances.Single(i => i.Name == "b"), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<SearchException>(() =>
            h.Search.SearchAsync(new SearchQuery("cats") { Engines = ["no_such_engine"] }, TestContext.Current.CancellationToken));

        Assert.Equal(SearchErrorKind.InvalidQuery, ex.Kind);
        Assert.Equal(nameof(SearchQuery.Engines), ex.Field);
        Assert.Empty(server.SearchRequestsTo("a.test"));
    }

    [Fact]
    public async Task An_engine_only_one_instance_offers_is_sent_there()
    {
        var server = new FakeSearxServer().On("a.test", NarrowSearxng(Ok)).OnSearxngJson("b.test", Ok);
        using var h = SearchHarness.Create(2, server: server);
        foreach (var instance in h.Selector.Instances)
        {
            await h.Capabilities.ProbeAsync(instance, TestContext.Current.CancellationToken);
        }

        await h.Search.SearchAsync(new SearchQuery("cats") { Engines = ["brave"] }, TestContext.Current.CancellationToken);

        Assert.Empty(server.SearchRequestsTo("a.test"));
        Assert.Single(server.SearchRequestsTo("b.test"));
    }

    [Fact]
    public async Task When_the_only_instance_offering_an_engine_is_down_it_is_an_outage_not_an_invalid_query()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok).On("b.test", NarrowSearxng(Ok));
        using var h = SearchHarness.Create(2, server: server);
        foreach (var instance in h.Selector.Instances)
        {
            await h.Capabilities.ProbeAsync(instance, TestContext.Current.CancellationToken);
        }

        OpenCircuit(h, "a");

        var ex = await Assert.ThrowsAsync<SearchException>(() =>
            h.Search.SearchAsync(new SearchQuery("cats") { Engines = ["brave"] }, TestContext.Current.CancellationToken));

        Assert.Equal(SearchErrorKind.NoEligibleInstance, ex.Kind);
        Assert.NotNull(ex.RetryAfter);
        Assert.Empty(server.SearchRequestsTo("b.test"));
    }

    [Fact]
    public async Task A_config_that_hangs_is_a_failed_read_after_one_attempt_not_an_exception()
    {
        var server = new FakeSearxServer().On("a.test", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return FakeSearxServer.Status(HttpStatusCode.OK);
        });
        using var h = SearchHarness.Create(1, new() { ["Timeouts:Attempt"] = "00:00:00.150" }, server, clock: TimeProvider.System);

        var snapshot = await h.Capabilities.ProbeAsync(h.Selector.Instances[0], TestContext.Current.CancellationToken);

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task Engines_are_matched_by_underscore_name_or_shortcut_and_still_sent()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok);
        using var h = SearchHarness.Create(1, server: server);
        await h.Capabilities.ProbeAsync(h.Selector.Instances[0], TestContext.Current.CancellationToken);

        await h.Search.SearchAsync(new SearchQuery("cats") { Engines = ["google_cse", "br"] }, TestContext.Current.CancellationToken);

        Assert.Single(server.SearchRequestsTo("a.test"));
    }

    [Fact]
    public async Task Without_a_fresh_snapshot_the_request_is_sent_anyway()
    {
        var server = new FakeSearxServer().On("a.test", (request, _) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/config" ? FakeSearxServer.Status(HttpStatusCode.ServiceUnavailable) : FakeSearxServer.Json(Ok)));
        using var h = SearchHarness.Create(1, server: server);

        var response = await h.Search.SearchAsync(new SearchQuery("cats") { Engines = ["no_such_engine"] }, TestContext.Current.CancellationToken);

        Assert.NotEmpty(response.Results);
    }

    [Fact]
    public async Task A_snapshot_that_is_not_searxng_shaped_is_not_kept()
    {
        var server = new FakeSearxServer().On("a.test", (_, _) => Task.FromResult(FakeSearxServer.Json("""{ "hello": "world" }""")));
        using var h = SearchHarness.Create(1, server: server);

        var snapshot = await h.Capabilities.ProbeAsync(h.Selector.Instances[0], TestContext.Current.CancellationToken);

        Assert.Null(snapshot);
        Assert.Null(h.Capabilities.GetFresh("a"));
    }

    [Fact]
    public async Task The_snapshot_expires_after_its_ttl()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok);
        using var h = SearchHarness.Create(1, server: server);
        await h.Capabilities.ProbeAsync(h.Selector.Instances[0], TestContext.Current.CancellationToken);
        Assert.NotNull(h.Capabilities.GetFresh("a"));

        h.Time.Advance(TimeSpan.FromMinutes(10));

        Assert.Null(h.Capabilities.GetFresh("a"));
    }

    // ---- privacy ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Per_instance_headers_are_sent_upstream()
    {
        string? seen = null;
        var server = new FakeSearxServer().OnSearxng("a.test", request =>
        {
            seen = request.Headers.TryGetValues("X-Proxy-Token", out var values) ? values.Single() : null;
            return FakeSearxServer.Json(Ok);
        });
        using var h = SearchHarness.Create(1, new() { ["Instances:0:Headers:X-Proxy-Token"] = "s3cret" }, server);

        await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.Equal("s3cret", seen);
    }

    [Fact]
    public async Task Failure_details_carry_instance_names_but_never_the_query_text()
    {
        var server = new FakeSearxServer().OnSearxng("a.test", _ => FakeSearxServer.Status(HttpStatusCode.InternalServerError));
        using var h = SearchHarness.Create(1, server: server);

        var ex = await Assert.ThrowsAsync<SearchException>(() => h.Search.SearchAsync(new SearchQuery("very private words"), TestContext.Current.CancellationToken));

        Assert.DoesNotContain("private", ex.Message);
        Assert.All(ex.Attempts, a => Assert.DoesNotContain("private", a.ToString()));
        Assert.Equal("a", Assert.Single(ex.Attempts).Instance);
    }

    private sealed class BrokenStream : Stream
    {
        private bool _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_sent)
            {
                throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
            }

            _sent = true;
            buffer[offset] = (byte)'{';
            return 1;
        }
    }

    private sealed class NonSeekableStream(string content) : Stream
    {
        private readonly MemoryStream _inner = new(Encoding.UTF8.GetBytes(content));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
