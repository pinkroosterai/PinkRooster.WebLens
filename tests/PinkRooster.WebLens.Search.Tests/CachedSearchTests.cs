using System.Net;

namespace PinkRooster.WebLens.Search.Tests;

/// <summary>Search caching: L1 only here (no Valkey), the real module against the fake network.</summary>
public class CachedSearchTests
{
    private static readonly string Ok = Fixture.Read(Fixture.Search);

    private static SearchHarness Cached(FakeSearxServer server) => SearchHarness.Create(1, new() { ["Cache:Enabled"] = "true" }, server);

    [Fact]
    public async Task An_identical_search_is_served_from_the_cache_and_says_so()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok);
        using var h = Cached(server);

        var first = await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);
        var second = await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.Single(server.SearchRequestsTo("a.test"));
        Assert.False(first.Meta.Cached);
        Assert.True(second.Meta.Cached);
        Assert.Equal(first.Results.Select(r => r.Url), second.Results.Select(r => r.Url));
    }

    [Fact]
    public async Task A_lookup_finds_a_stored_search_trimmed_to_its_limit_and_never_asks_upstream()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok);
        using var h = Cached(server);
        var ct = TestContext.Current.CancellationToken;

        var before = await h.Search.TryGetCachedAsync(new SearchQuery("cats") { Limit = 50 }, ct);
        await h.Search.SearchAsync(new SearchQuery("cats") { Limit = 50 }, ct);
        var hit = await h.Search.TryGetCachedAsync(new SearchQuery("cats") { Limit = 3 }, ct);

        Assert.Null(before);
        Assert.Single(server.SearchRequestsTo("a.test"));
        Assert.NotNull(hit);
        Assert.True(hit.Meta.Cached);
        Assert.Equal(3, hit.Results.Count);
        await Assert.ThrowsAsync<SearchException>(() => h.Search.TryGetCachedAsync(new SearchQuery(" "), ct));
    }

    [Fact]
    public async Task One_entry_serves_every_limit_because_trimming_sits_above_the_cache()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok);
        using var h = Cached(server);

        var three = await h.Search.SearchAsync(new SearchQuery("cats") { Limit = 3 }, TestContext.Current.CancellationToken);
        var all = await h.Search.SearchAsync(new SearchQuery("cats") { Limit = 50 }, TestContext.Current.CancellationToken);

        Assert.Single(server.SearchRequestsTo("a.test"));
        Assert.Equal(3, three.Results.Count);
        Assert.True(all.Results.Count > 3);
    }

    [Fact]
    public async Task Different_queries_are_different_entries()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok);
        using var h = Cached(server);

        await h.Search.SearchAsync(new SearchQuery("cats"), TestContext.Current.CancellationToken);
        await h.Search.SearchAsync(new SearchQuery("cats") { Page = 2 }, TestContext.Current.CancellationToken);
        await h.Search.SearchAsync(new SearchQuery("cats") { Engines = ["brave"] }, TestContext.Current.CancellationToken);

        Assert.Equal(3, server.SearchRequestsTo("a.test").Count);
    }

    [Fact]
    public async Task A_failure_is_never_cached()
    {
        var failing = true;
        var server = new FakeSearxServer().OnSearxng("a.test", _ => failing ? FakeSearxServer.Status(HttpStatusCode.InternalServerError) : FakeSearxServer.Json(Ok));
        using var h = Cached(server);

        await Assert.ThrowsAsync<SearchException>(() => h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken));
        failing = false;
        var response = await h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken);

        Assert.False(response.Meta.Cached);
        Assert.Equal(2, server.SearchRequestsTo("a.test").Count);
    }

    [Fact]
    public async Task An_invalid_query_is_refused_before_any_lookup()
    {
        var server = new FakeSearxServer().OnSearxngJson("a.test", Ok);
        using var h = Cached(server);

        var ex = await Assert.ThrowsAsync<SearchException>(() => h.Search.SearchAsync(new SearchQuery("cats") { Engines = ["!bad"] }, TestContext.Current.CancellationToken));

        Assert.Equal(SearchErrorKind.InvalidQuery, ex.Kind);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Concurrent_identical_searches_share_one_upstream_request()
    {
        var gate = new TaskCompletionSource();
        var server = new FakeSearxServer().On("a.test", async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/search")
            {
                await gate.Task.WaitAsync(ct);
            }

            return FakeSearxServer.Json(request.RequestUri.AbsolutePath == "/config" ? Fixture.Read(Fixture.Config) : Ok);
        });
        using var h = Cached(server);

        var searches = Enumerable.Range(0, 3).Select(_ => h.Search.SearchAsync(SearchHarness.Query(), TestContext.Current.CancellationToken)).ToList();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        gate.SetResult();
        var responses = await Task.WhenAll(searches);

        Assert.Single(server.SearchRequestsTo("a.test"));
        Assert.All(responses, r => Assert.NotEmpty(r.Results));
    }

    [Fact]
    public void The_key_is_a_hash_and_never_contains_the_query_text()
    {
        var key = CachedSearchService.Key(QueryNormalizer.Normalize(new SearchQuery("very private words"), 512));

        Assert.StartsWith("weblens:search:", key);
        Assert.DoesNotContain("private", key);
        Assert.Equal(key, CachedSearchService.Key(QueryNormalizer.Normalize(new SearchQuery("very private words") { Limit = 3 }, 512)));
    }
}
