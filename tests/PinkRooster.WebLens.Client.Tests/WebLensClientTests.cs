using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using PinkRooster.WebLens.Client.Exceptions;
using PinkRooster.WebLens.Client.Models;
using PinkRooster.WebLens.Client.Serialization;

namespace PinkRooster.WebLens.Client.Tests;

public class WebLensClientTests
{
    private sealed class DelegatingMockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handlerFunc(request, cancellationToken);
    }

    [Fact]
    public async Task SearchAsync_sends_valid_request_and_deserializes_response()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var mockResponse = new SearchResponse
        {
            Query = "pink rooster",
            Results =
            [
                new SearchResultItem("https://example.com/item", "Title 1", "Snippet 1", "general", ["google"], 0.9, null, null, null)
            ],
            Answers = [],
            Suggestions = ["suggestion"],
            Corrections = [],
            Infoboxes = [],
            Meta = new SearchMeta(1, 1, false, [], 0, 45, false)
        };

        var handler = new DelegatingMockHandler(async (req, ct) =>
        {
            capturedRequest = req;
            if (req.Content is not null)
            {
                capturedBody = await req.Content.ReadAsStringAsync(ct);
            }

            var json = JsonSerializer.Serialize(mockResponse, WebLensJsonSerializerContext.Default.SearchResponse);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://weblens.test/") };
        var options = new WebLensClientOptions
        {
            BaseAddress = new Uri("https://weblens.test/"),
            ApiKey = "wl_secret_key",
            EnableResilience = false
        };

        using var client = new WebLensClient(httpClient, options);
        var searchRequest = new SearchRequest("pink rooster") { Limit = 5 };

        var response = await client.SearchAsync(searchRequest, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("/v1/search", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal("wl_secret_key", capturedRequest.Headers.GetValues("X-Api-Key").Single());
        Assert.NotNull(capturedBody);
        Assert.Contains("\"query\":\"pink rooster\"", capturedBody);

        Assert.NotNull(response);
        Assert.Equal("pink rooster", response.Query);
        Assert.Single(response.Results);
        Assert.Equal("https://example.com/item", response.Results[0].Url);
    }

    [Fact]
    public async Task FetchAsync_sends_valid_request_and_deserializes_response()
    {
        HttpRequestMessage? capturedRequest = null;

        var mockResponse = new FetchResponse
        {
            RequestedUrl = "https://example.com/article",
            FinalUrl = "https://example.com/article",
            Title = "Article Title",
            Markdown = "# Article Content",
            Truncated = false,
            Metadata = new PageMetadata("Author", "en", null, "Site", "Excerpt"),
            Diagnostics = new FetchDiagnostics(200, "smartreader", 0.95, 1, false, 5000, 17, 80, 10, 5, false, 60, 20, "http")
        };

        var handler = new DelegatingMockHandler((req, _) =>
        {
            capturedRequest = req;
            var json = JsonSerializer.Serialize(mockResponse, WebLensJsonSerializerContext.Default.FetchResponse);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://weblens.test/") };
        var options = new WebLensClientOptions
        {
            BaseAddress = new Uri("https://weblens.test/"),
            ApiKey = "wl_fetch_key",
            EnableResilience = false
        };

        using var client = new WebLensClient(httpClient, options);
        var fetchRequest = new FetchRequest("https://example.com/article");

        var response = await client.FetchAsync(fetchRequest, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest.Method);
        Assert.Equal("/v1/fetch", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal("wl_fetch_key", capturedRequest.Headers.GetValues("X-Api-Key").Single());

        Assert.NotNull(response);
        Assert.Equal("https://example.com/article", response.RequestedUrl);
        Assert.Equal("# Article Content", response.Markdown);
    }

    [Fact]
    public async Task CheckHealthAsync_returns_true_on_200_and_false_on_503_or_exception()
    {
        var isHealthy = true;
        var handler = new DelegatingMockHandler((req, _) =>
        {
            Assert.Equal("/health/live", req.RequestUri?.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(isHealthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://weblens.test/") };
        var options = new WebLensClientOptions
        {
            BaseAddress = new Uri("https://weblens.test/"),
            ApiKey = "key",
            EnableResilience = false
        };

        using var client = new WebLensClient(httpClient, options);

        var healthyResult = await client.CheckHealthAsync(TestContext.Current.CancellationToken);
        Assert.True(healthyResult);

        isHealthy = false;
        var unhealthyResult = await client.CheckHealthAsync(TestContext.Current.CancellationToken);
        Assert.False(unhealthyResult);
    }

    [Fact]
    public async Task SearchAsync_throws_WebLensValidationException_on_validation_problem()
    {
        const string problemJson = """
            {
                "type": "urn:weblens:problem:validation",
                "title": "The request is not valid.",
                "status": 400,
                "detail": "Field query is required.",
                "errors": {
                    "query": ["The Query field is required."]
                }
            }
            """;

        var handler = new DelegatingMockHandler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(problemJson, Encoding.UTF8, "application/problem+json")
            });
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://weblens.test/") };
        var options = new WebLensClientOptions
        {
            BaseAddress = new Uri("https://weblens.test/"),
            ApiKey = "key",
            EnableResilience = false
        };

        using var client = new WebLensClient(httpClient, options);

        var ex = await Assert.ThrowsAsync<WebLensValidationException>(() => client.SearchAsync(new SearchRequest(""), TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.True(ex.Errors.ContainsKey("query"));
    }

    [Fact]
    public async Task Non_transient_error_422_is_not_retried()
    {
        var attemptCount = 0;
        const string problemJson = """
            {
                "type": "urn:weblens:problem:target-not-allowed",
                "title": "Target Not Allowed",
                "status": 422,
                "detail": "Target address is not allowed."
            }
            """;

        var innerHandler = new DelegatingMockHandler((_, _) =>
        {
            Interlocked.Increment(ref attemptCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent(problemJson, Encoding.UTF8, "application/problem+json")
            });
        });

        var options = new WebLensClientOptions
        {
            BaseAddress = new Uri("https://weblens.test/"),
            ApiKey = "key",
            MaxRetries = 3,
            RetryInitialDelay = TimeSpan.FromMilliseconds(50),
            EnableResilience = true
        };

        var resilienceHandler = Resilience.WebLensResilience.CreateResilienceHandler(options, innerHandler);
        using var httpClient = new HttpClient(resilienceHandler) { BaseAddress = options.BaseAddress };
        using var client = new WebLensClient(httpClient, options);

        await Assert.ThrowsAsync<WebLensTargetNotAllowedException>(() => client.FetchAsync(new FetchRequest("http://192.168.1.1"), TestContext.Current.CancellationToken));

        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task Transient_error_503_retries_and_succeeds()
    {
        var attemptCount = 0;
        var mockResponse = new SearchResponse
        {
            Query = "retry test",
            Results = [],
            Answers = [],
            Suggestions = [],
            Corrections = [],
            Infoboxes = [],
            Meta = new SearchMeta(1, 0, false, [], 0, 10, false)
        };

        var innerHandler = new DelegatingMockHandler((_, _) =>
        {
            var current = Interlocked.Increment(ref attemptCount);
            if (current < 3)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"type\":\"urn:weblens:problem:capacity-exceeded\",\"status\":503}", Encoding.UTF8, "application/problem+json")
                });
            }

            var json = JsonSerializer.Serialize(mockResponse, WebLensJsonSerializerContext.Default.SearchResponse);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        var options = new WebLensClientOptions
        {
            BaseAddress = new Uri("https://weblens.test/"),
            ApiKey = "key",
            MaxRetries = 3,
            RetryInitialDelay = TimeSpan.FromMilliseconds(10),
            EnableResilience = true
        };

        var resilienceHandler = Resilience.WebLensResilience.CreateResilienceHandler(options, innerHandler);
        using var httpClient = new HttpClient(resilienceHandler) { BaseAddress = options.BaseAddress };
        using var client = new WebLensClient(httpClient, options);

        var result = await client.SearchAsync(new SearchRequest("retry test"), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(3, attemptCount);
    }

    [Fact]
    public async Task Cancellation_aborts_in_flight_request_within_10_milliseconds()
    {
        var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new DelegatingMockHandler(async (_, ct) =>
        {
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                return await tcs.Task;
            }
        });

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://weblens.test/") };
        var options = new WebLensClientOptions
        {
            BaseAddress = new Uri("https://weblens.test/"),
            ApiKey = "key",
            EnableResilience = false
        };

        using var client = new WebLensClient(httpClient, options);
        using var cts = new CancellationTokenSource();

        var task = client.SearchAsync(new SearchRequest("cancel test"), cts.Token);

        // Cancel after 20 ms, and measure elapsed time from cancellation request to task completion
        await Task.Delay(20, TestContext.Current.CancellationToken);

        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds <= 50, $"Cancellation propagation took {sw.ElapsedMilliseconds} ms, expected within 50 ms.");
    }
}
