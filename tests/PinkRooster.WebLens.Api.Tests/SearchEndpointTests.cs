using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PinkRooster.WebLens.Api.Errors;
using PinkRooster.WebLens.Search;

namespace PinkRooster.WebLens.Api.Tests;

public class SearchEndpointTests
{
    private static async Task<(HttpResponseMessage Response, JsonElement Body)> PostAsync(WebLensFactory factory, string json)
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsync("/v1/search", new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static WebLensFactory Returning(SearchResponse response) => new() { SearchBehavior = (_, _) => Task.FromResult(response) };

    private static WebLensFactory Throwing(Exception exception) => new() { SearchBehavior = (_, _) => throw exception };

    // ---- success and contract shape -------------------------------------------------------------------------

    [Fact]
    public async Task Returns_the_documented_camel_case_shape()
    {
        await using var factory = Returning(Samples.Response());

        var (response, body) = await PostAsync(factory, """{ "query": "cats" }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cats", body.GetProperty("query").GetString());
        var result = body.GetProperty("results")[0];
        Assert.Equal("https://example.org/a", result.GetProperty("url").GetString());
        Assert.Equal("Example A", result.GetProperty("title").GetString());
        Assert.Equal("About A", result.GetProperty("snippet").GetString());
        Assert.Equal(["brave", "duckduckgo"], result.GetProperty("engines").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1.5, result.GetProperty("score").GetDouble());
        Assert.Equal("2026-09-05T10:34:41+00:00", result.GetProperty("publishedAt").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("thumbnailUrl").ValueKind);
        Assert.Equal("42", body.GetProperty("answers")[0].GetProperty("text").GetString());
        Assert.Equal("cat food", body.GetProperty("suggestions")[0].GetString());
        Assert.Equal("cats", body.GetProperty("corrections")[0].GetString());
        Assert.Equal("Cat", body.GetProperty("infoboxes")[0].GetProperty("title").GetString());

        var meta = body.GetProperty("meta");
        Assert.Equal(1, meta.GetProperty("page").GetInt32());
        Assert.Equal(1, meta.GetProperty("resultCount").GetInt32());
        Assert.True(meta.GetProperty("partial").GetBoolean());
        Assert.Equal("brave", meta.GetProperty("engineFailures")[0].GetProperty("engine").GetString());
        Assert.Equal(2, meta.GetProperty("droppedItems").GetInt32());
        Assert.Equal(12, meta.GetProperty("elapsedMs").GetInt64());
        Assert.False(meta.GetProperty("cached").GetBoolean());
    }

    [Fact]
    public async Task Never_reveals_which_instance_served_the_request()
    {
        await using var factory = Returning(Samples.Response());

        var (_, body) = await PostAsync(factory, """{ "query": "cats" }""");

        Assert.DoesNotContain("a.test", body.GetRawText());
    }

    [Fact]
    public async Task Translates_the_request_into_a_module_query_with_defaults()
    {
        await using var factory = Returning(Samples.Response());

        await PostAsync(factory, """
            { "query": "cats", "categories": ["news"], "language": "en-GB", "page": 2,
              "timeRange": "month", "safeSearch": "strict", "engines": ["google_cse"], "limit": 5 }
            """);

        var query = Assert.Single(factory.SeenQueries);
        Assert.Equal("cats", query.Text);
        Assert.Equal(["news"], query.Categories);
        Assert.Equal("en-GB", query.Language);
        Assert.Equal(2, query.Page);
        Assert.Equal(SearchTimeRange.Month, query.TimeRange);
        Assert.Equal(SearchSafeSearch.Strict, query.SafeSearch);
        Assert.Equal(["google_cse"], query.Engines);
        Assert.Equal(5, query.Limit);
    }

    [Fact]
    public async Task Defaults_are_page_1_and_limit_10()
    {
        await using var factory = Returning(Samples.Response());

        await PostAsync(factory, """{ "query": "cats" }""");

        var query = Assert.Single(factory.SeenQueries);
        Assert.Equal(1, query.Page);
        Assert.Equal(10, query.Limit);
        Assert.Null(query.TimeRange);
    }

    [Fact]
    public async Task Unknown_request_properties_are_ignored()
    {
        await using var factory = Returning(Samples.Response());

        var (response, _) = await PostAsync(factory, """{ "query": "cats", "somethingNew": { "x": 1 } }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- 400 validation ---------------------------------------------------------------------------------------

    public static TheoryData<string, string> InvalidBodies => new()
    {
        { """{}""", "query" },
        { """{ "query": "" }""", "query" },
        { """{ "query": "   " }""", "query" },
        { """{ "query": "cats", "page": 0 }""", "page" },
        { """{ "query": "cats", "page": 11 }""", "page" },
        { """{ "query": "cats", "limit": 51 }""", "limit" },
        { """{ "query": "cats", "engines": ["a", "b", "c", "d", "e", "f", "g", "h", "i"] }""", "Engines" },
        { """{ "query": "cats", "timeRange": "decade" }""", "timeRange" },
    };

    /// <summary>Content rules the search module owns; the host must report them in the same validation shape.</summary>
    public static TheoryData<string, string> BodiesTheModuleRejects => new()
    {
        { """{ "query": "cats", "engines": ["!wp"] }""", "Engines" },
        { """{ "query": "cats", "engines": ["a b"] }""", "Engines" },
        { """{ "query": "cats", "categories": ["ok", "bad:token"] }""", "Categories" },
        { """{ "query": "cats", "language": "en us" }""", "Language" },
        { $$"""{ "query": "{{new string('x', 513)}}" }""", "Query" },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Invalid_requests_are_a_400_validation_problem_and_never_reach_the_module(string json, string field)
    {
        await using var factory = Returning(Samples.Response());

        var (response, body) = await PostAsync(factory, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("urn:weblens:problem:validation", body.GetProperty("type").GetString());
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("Validation", body.GetProperty("errorKind").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        Assert.Empty(factory.SeenQueries);
        if (body.TryGetProperty("errors", out var errors))
        {
            Assert.Contains(errors.EnumerateObject(), e => e.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    public async Task A_malformed_or_empty_body_is_a_400_validation_problem(string json)
    {
        await using var factory = Returning(Samples.Response());

        var (response, body) = await PostAsync(factory, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("urn:weblens:problem:validation", body.GetProperty("type").GetString());
        Assert.Empty(factory.SeenQueries);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task A_malformed_body_is_a_400_validation_problem_in_every_environment(string environment)
    {
        // ASP.NET Core throws on unbindable bodies in Development and answers 400 elsewhere; the contract must not depend on that.
        await using var factory = new WebLensFactory { Environment = environment, SearchBehavior = (_, _) => Task.FromResult(Samples.Response()) };

        var (response, body) = await PostAsync(factory, "{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("urn:weblens:problem:validation", body.GetProperty("type").GetString());
    }

    [Theory]
    [MemberData(nameof(BodiesTheModuleRejects))]
    public async Task Content_rules_are_the_modules_and_come_back_as_the_same_validation_problem(string json, string field)
    {
        var network = new DelegateHandler((_, _) => Task.FromResult(Samples.Json(Samples.SearxJson)));
        await using var factory = new WebLensFactory { Network = network };

        var (response, body) = await PostAsync(factory, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("urn:weblens:problem:validation", body.GetProperty("type").GetString());
        Assert.Equal("The request is not valid.", body.GetProperty("title").GetString());
        Assert.Equal("InvalidQuery", body.GetProperty("errorKind").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        Assert.Equal(field, Assert.Single(body.GetProperty("errors").EnumerateObject()).Name);
        Assert.Equal(0, network.Calls);
    }

    [Theory]
    [InlineData(nameof(SearchQuery.Engines), "Engines")]
    [InlineData(nameof(SearchQuery.Text), "Query")]
    [InlineData(null, "Query")]
    public async Task A_module_side_invalid_query_names_the_request_field(string? moduleField, string contractField)
    {
        await using var factory = Throwing(new SearchException(SearchErrorKind.InvalidQuery, "Not offered.") { Field = moduleField });

        var (response, body) = await PostAsync(factory, """{ "query": "cats" }""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("urn:weblens:problem:validation", body.GetProperty("type").GetString());
        Assert.Equal("Not offered.", body.GetProperty("errors").GetProperty(contractField)[0].GetString());
    }

    [Fact]
    public async Task The_query_length_limit_follows_the_module_configuration()
    {
        await using var factory = new WebLensFactory { Network = new DelegateHandler((_, _) => Task.FromResult(Samples.Json(Samples.SearxJson))) };
        factory.Settings["WebLens:Search:Query:MaxLength"] = "1000";

        var (response, _) = await PostAsync(factory, $$"""{ "query": "{{new string('x', 800)}}" }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- error table rows -------------------------------------------------------------------------------

    [Fact]
    public async Task Unsupported_query_syntax_is_a_400_search_query_unsupported()
    {
        await using var factory = Throwing(new SearchException(SearchErrorKind.QueryUnsupported, "The query asks the search instance to redirect to an external site."));

        var (response, body) = await PostAsync(factory, """{ "query": "!!ddg cats" }""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("urn:weblens:problem:search-query-unsupported", body.GetProperty("type").GetString());
        Assert.Equal("QueryUnsupported", body.GetProperty("errorKind").GetString());
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Every_instance_failing_is_a_502_search_failed()
    {
        await using var factory = Throwing(new SearchException(SearchErrorKind.AllAttemptsFailed, "Every attempted search instance failed.", [new AttemptSummary("a", "ServerError", 500, 1)]));

        var (response, body) = await PostAsync(factory, """{ "query": "cats" }""");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("urn:weblens:problem:search-failed", body.GetProperty("type").GetString());
        Assert.Equal("AllAttemptsFailed", body.GetProperty("errorKind").GetString());
        Assert.DoesNotContain("ServerError", body.GetRawText());
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task No_eligible_instance_is_a_503_search_unavailable_with_retry_after()
    {
        await using var factory = Throwing(new SearchException(SearchErrorKind.NoEligibleInstance, "No search instance is currently eligible.", retryAfter: TimeSpan.FromSeconds(29.2)));

        var (response, body) = await PostAsync(factory, """{ "query": "cats" }""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("urn:weblens:problem:search-unavailable", body.GetProperty("type").GetString());
        Assert.Equal("30", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal(30, body.GetProperty("retryAfterSeconds").GetInt32());
    }

    [Fact]
    public async Task No_eligible_instance_without_a_recovery_time_has_no_retry_after()
    {
        await using var factory = Throwing(new SearchException(SearchErrorKind.NoEligibleInstance, "No search instance is currently eligible."));

        var (response, body) = await PostAsync(factory, """{ "query": "cats" }""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("retryAfterSeconds").ValueKind);
    }

    [Fact]
    public async Task A_module_deadline_is_a_504_timeout()
    {
        await using var factory = Throwing(new SearchException(SearchErrorKind.Timeout, "The search did not finish within its time budget."));

        var (response, body) = await PostAsync(factory, """{ "query": "cats" }""");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("urn:weblens:problem:timeout", body.GetProperty("type").GetString());
        Assert.Equal("Timeout", body.GetProperty("errorKind").GetString());
    }

    [Fact]
    public async Task An_unexpected_exception_is_a_500_internal_problem_without_exception_text()
    {
        await using var factory = Throwing(new InvalidOperationException("secret connection string password=hunter2"));

        var (response, body) = await PostAsync(factory, """{ "query": "cats" }""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("urn:weblens:problem:internal", body.GetProperty("type").GetString());
        Assert.Equal("Internal", body.GetProperty("errorKind").GetString());
        Assert.DoesNotContain("hunter2", body.GetRawText());
    }

    [Fact]
    public async Task A_client_that_disconnects_gets_no_problem_written()
    {
        var handler = new ClientDisconnectedExceptionHandler();

        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestAborted = aborted.Token };

        var handled = await handler.TryHandleAsync(context, new OperationCanceledException(), TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task An_operation_cancelled_exception_while_the_client_is_still_connected_is_not_swallowed()
    {
        var handler = new ClientDisconnectedExceptionHandler();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();

        var handled = await handler.TryHandleAsync(context, new OperationCanceledException(), TestContext.Current.CancellationToken);

        Assert.False(handled);
    }

    // ---- end to end: the real search module behind the real host ----------------------------------------

    [Fact]
    public async Task End_to_end_a_real_search_maps_the_upstream_json()
    {
        await using var factory = new WebLensFactory
        {
            Network = new DelegateHandler((_, _) => Task.FromResult(Samples.Json(Samples.SearxJson))),
        };

        var (response, body) = await PostAsync(factory, """{ "query": "cats", "limit": 5 }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://example.org/a", body.GetProperty("results")[0].GetProperty("url").GetString());
        Assert.False(body.GetProperty("meta").GetProperty("partial").GetBoolean());
    }

    [Fact]
    public async Task End_to_end_an_upstream_500_is_a_502_search_failed()
    {
        await using var factory = new WebLensFactory
        {
            Network = new DelegateHandler((_, _) => Task.FromResult(Samples.Json("oops", HttpStatusCode.InternalServerError))),
        };

        var (response, body) = await PostAsync(factory, """{ "query": "cats" }""");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("urn:weblens:problem:search-failed", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task End_to_end_a_cooling_down_instance_is_a_503_search_unavailable_with_retry_after()
    {
        await using var factory = new WebLensFactory
        {
            Network = new DelegateHandler((_, _) => Task.FromResult(Samples.Json("slow down", HttpStatusCode.TooManyRequests))),
        };

        var (response, body) = await PostAsync(factory, """{ "query": "cats" }""");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("urn:weblens:problem:search-unavailable", body.GetProperty("type").GetString());
        Assert.True(int.Parse(Assert.Single(response.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task End_to_end_an_external_bang_redirect_is_a_400_search_query_unsupported()
    {
        await using var factory = new WebLensFactory
        {
            Network = new DelegateHandler((_, _) =>
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("http://duckduckgo.com/?q=cats");
                return Task.FromResult(redirect);
            }),
        };

        var (response, body) = await PostAsync(factory, """{ "query": "!!ddg cats" }""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("urn:weblens:problem:search-query-unsupported", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task End_to_end_a_search_that_outlives_its_budget_is_a_504_timeout()
    {
        await using var factory = new WebLensFactory
        {
            Network = new DelegateHandler(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return Samples.Json(Samples.SearxJson);
            }),
        };
        factory.Settings["WebLens:Search:Timeouts:Attempt"] = "00:00:00.400";
        factory.Settings["WebLens:Search:Timeouts:Total"] = "00:00:00.500";
        factory.Settings["WebLens:Search:Instances:1:Name"] = "b";
        factory.Settings["WebLens:Search:Instances:1:BaseUri"] = "https://b.test";
        factory.Settings["WebLens:Search:Instances:1:Priority"] = "1";

        var (response, body) = await PostAsync(factory, """{ "query": "cats" }""");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("urn:weblens:problem:timeout", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task End_to_end_a_misconfigured_host_refuses_to_start()
    {
        var ex = await HostStart.FailureAsync("Testing", new()
        {
            ["WebLens:Search:Timeouts:Attempt"] = "00:00:09",
            ["WebLens:Fetch:Security:RequireEgressProxy"] = "false",
        });

        Assert.Contains("Attempt must be shorter than Total", ex.ToString());
    }
}
