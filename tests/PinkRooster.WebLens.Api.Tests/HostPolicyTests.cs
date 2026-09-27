using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PinkRooster.WebLens.Fetch;
using PinkRooster.WebLens.Search;

namespace PinkRooster.WebLens.Api.Tests;

/// <summary>Keys and scopes, per-key limits and the host's timeout backstop: the 401, 403, 429 and host-504 rows of the error table.</summary>
public class HostPolicyTests
{
    private static async Task<(HttpResponseMessage Response, JsonElement Body)> PostAsync(WebLensFactory factory, string path, string json, string? key = WebLensFactory.AllScopesKey)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }

        var response = await client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static WebLensFactory Working() => new()
    {
        SearchBehavior = (_, _) => Task.FromResult(Samples.Response()),
        FetchBehavior = (_, _) => Task.FromResult(FetchSamples.Result()),
    };

    private const string SearchBody = """{ "query": "cats" }""";
    private const string FetchBody = """{ "url": "https://example.org/" }""";

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-key")]
    [InlineData("")]
    public async Task A_missing_or_wrong_key_is_a_401_with_the_same_body_whatever_the_cause(string? key)
    {
        await using var factory = Working();

        var (response, body) = await PostAsync(factory, "/v1/search", SearchBody, key);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("urn:weblens:problem:unauthorized", body.GetProperty("type").GetString());
        Assert.Equal("A valid API key is required.", body.GetProperty("title").GetString());
        Assert.Equal("Send the key in the X-Api-Key header.", body.GetProperty("detail").GetString());
        Assert.Empty(factory.SeenQueries);
    }

    [Fact]
    public async Task A_key_without_the_scope_is_a_403()
    {
        await using var factory = Working();

        var (searchResponse, _) = await PostAsync(factory, "/v1/search", SearchBody, WebLensFactory.SearchOnlyKey);
        var (fetchResponse, body) = await PostAsync(factory, "/v1/fetch", FetchBody, WebLensFactory.SearchOnlyKey);

        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, fetchResponse.StatusCode);
        Assert.Equal("urn:weblens:problem:forbidden", body.GetProperty("type").GetString());
        Assert.Empty(factory.SeenFetches);
    }

    [Fact]
    public async Task Health_live_needs_no_key()
    {
        await using var factory = Working();
        factory.ApiKey = null;
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Too_many_searches_from_one_key_are_a_429_with_retry_after_and_other_keys_are_unaffected()
    {
        await using var factory = Working();
        factory.Settings["WebLens:Api:RateLimitProfiles:default:SearchBurst"] = "2";
        factory.Settings["WebLens:Api:RateLimitProfiles:default:SearchPerMinute"] = "1";

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            codes.Add((await PostAsync(factory, "/v1/search", SearchBody)).Response.StatusCode);
        }

        var (limited, body) = await PostAsync(factory, "/v1/search", SearchBody);
        var (other, _) = await PostAsync(factory, "/v1/search", SearchBody, WebLensFactory.SearchOnlyKey);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], codes);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("urn:weblens:problem:rate-limited", body.GetProperty("type").GetString());
        Assert.True(int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture) >= 1);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task A_key_gets_its_per_minute_rate_not_only_its_burst()
    {
        await using var factory = Working();
        factory.Settings["WebLens:Api:RateLimitProfiles:default:SearchBurst"] = "1";
        factory.Settings["WebLens:Api:RateLimitProfiles:default:SearchPerMinute"] = "60";

        var first = (await PostAsync(factory, "/v1/search", SearchBody)).Response;
        var limited = (await PostAsync(factory, "/v1/search", SearchBody)).Response;

        // 60 a minute is one token a second: the next one is due within the second, not at the next minute.
        await Task.Delay(TimeSpan.FromMilliseconds(1_200), TestContext.Current.CancellationToken);
        var refilled = (await PostAsync(factory, "/v1/search", SearchBody)).Response;

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("1", Assert.Single(limited.Headers.GetValues("Retry-After")));
        Assert.Equal(HttpStatusCode.OK, refilled.StatusCode);
    }

    [Fact]
    public async Task Cache_hits_cost_no_permit_and_misses_do()
    {
        await using var factory = Working();
        factory.SearchCache = q => q.Text == "cached" ? Samples.Response("cached") : null;
        factory.FetchCache = r => r.Url == "https://example.org/cached" ? FetchSamples.Result() : null;
        factory.Settings["WebLens:Api:RateLimitProfiles:default:SearchBurst"] = "1";
        factory.Settings["WebLens:Api:RateLimitProfiles:default:SearchPerMinute"] = "1";
        factory.Settings["WebLens:Api:RateLimitProfiles:default:FetchBurst"] = "1";
        factory.Settings["WebLens:Api:RateLimitProfiles:default:FetchPerMinute"] = "1";
        const string CachedSearch = """{ "query": "cached" }""";
        const string CachedFetch = """{ "url": "https://example.org/cached" }""";

        var codes = new List<HttpStatusCode>();
        foreach (var (path, body) in new[]
        {
            ("/v1/search", SearchBody), ("/v1/search", CachedSearch), ("/v1/search", CachedSearch), ("/v1/search", SearchBody),
            ("/v1/fetch", FetchBody), ("/v1/fetch", CachedFetch), ("/v1/fetch", CachedFetch), ("/v1/fetch", FetchBody),
        })
        {
            codes.Add((await PostAsync(factory, path, body)).Response.StatusCode);
        }

        // One token each: the first miss spends it, the hits are free, the second miss is refused.
        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests,
             HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests],
            codes);
        Assert.Single(factory.SeenQueries);
        Assert.Single(factory.SeenFetches);
    }

    [Fact]
    public async Task A_fetch_refused_for_concurrency_does_not_spend_a_token()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        await using var factory = new WebLensFactory
        {
            FetchBehavior = async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return FetchSamples.Result();
            },
        };
        factory.Settings["WebLens:Api:RateLimitProfiles:default:FetchConcurrency"] = "1";
        factory.Settings["WebLens:Api:RateLimitProfiles:default:FetchBurst"] = "2";
        factory.Settings["WebLens:Api:RateLimitProfiles:default:FetchPerMinute"] = "1";

        var first = PostAsync(factory, "/v1/fetch", FetchBody);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var (second, _) = await PostAsync(factory, "/v1/fetch", FetchBody);
        release.SetResult();
        var (firstResponse, _) = await first;
        var (third, _) = await PostAsync(factory, "/v1/fetch", FetchBody);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    /// <summary>WebApplicationFactory's in-memory server is not Kestrel and ignores its limits, so the configuration itself is asserted.</summary>
    [Fact]
    public async Task Kestrel_limits_and_the_shutdown_timeout_are_set()
    {
        await using var factory = Working();
        using var _ = factory.CreateClient();

        var kestrel = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>>().Value;
        var host = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Hosting.HostOptions>>().Value;

        Assert.Equal(64 * 1024, kestrel.Limits.MaxRequestBodySize);
        Assert.Equal(64, kestrel.Limits.MaxRequestHeaderCount);
        Assert.Equal(TimeSpan.FromSeconds(10), kestrel.Limits.RequestHeadersTimeout);
        Assert.False(kestrel.AddServerHeader);
        Assert.True(host.ShutdownTimeout > TimeSpan.FromSeconds(30), "shutdown must outlast a running fetch");
    }

    [Fact]
    public async Task The_hosts_request_timeout_is_a_504_timeout_problem()
    {
        await using var factory = new WebLensFactory
        {
            SearchBehavior = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Samples.Response();
            },
        };
        factory.Settings["WebLens:Api:SearchRequestTimeout"] = "00:00:00.300";

        var (response, body) = await PostAsync(factory, "/v1/search", SearchBody);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("urn:weblens:problem:timeout", body.GetProperty("type").GetString());
    }
}

internal static class FetchSamples
{
    public static FetchResult Result() => new(
        new Uri("https://example.org/"),
        new Uri("https://example.org/"),
        "Title",
        "# Title\n",
        false,
        new Fetch.PageMetadata(null, "en", null, null, null),
        new Fetch.FetchDiagnostics(200, "smartreader", 0.9, 1, false, 100, 8, 10, 1, 1, false));
}
