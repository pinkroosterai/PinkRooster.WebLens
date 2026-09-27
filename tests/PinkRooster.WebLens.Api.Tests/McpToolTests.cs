using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using PinkRooster.WebLens.Fetch;
using PinkRooster.WebLens.Search;

namespace PinkRooster.WebLens.Api.Tests;

/// <summary>
/// The MCP front door through the SDK's own client: the two tools, their argument rules, every
/// module row of the error table as an error result, paging, keys, scopes and the shared per-key limits.
/// </summary>
public class McpToolTests
{
    private static WebLensFactory Working(string markdown = "# Title\n") => new()
    {
        SearchBehavior = (_, _) => Task.FromResult(Samples.Response()),
        FetchBehavior = (_, _) => Task.FromResult(Page(markdown)),
    };

    private static FetchResult Page(string markdown, bool truncated = false) => FetchSamples.Result() with
    {
        FinalUrl = new Uri("https://example.org/final"),
        Markdown = markdown,
        Truncated = truncated,
    };

    private static readonly string[] Nav = ["nav"];
    private static readonly string[] News = ["news"];
    private static readonly string[] Brave = ["brave"];

    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] arguments) =>
        arguments.ToDictionary(a => a.Name, a => a.Value);

    [Fact]
    public async Task Lists_exactly_the_two_tools()
    {
        await using var factory = Working();
        await using var client = await McpSupport.ConnectAsync(factory);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["fetch", "search"], tools.Select(t => t.Name).Order());
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
    }

    [Fact]
    public async Task Search_answers_in_text_and_in_the_http_shape_both_with_the_untrusted_line()
    {
        await using var factory = Working();
        await using var client = await McpSupport.ConnectAsync(factory);
        using var http = factory.CreateClient();

        var result = await client.CallAsync("search", Args(("query", "cats")));
        var viaHttp = await http.PostAsync("/v1/search", new StringContent("""{ "query": "cats" }""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var httpBody = JsonDocument.Parse(await viaHttp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        Assert.NotEqual(true, result.IsError);
        var text = result.Text();
        Assert.StartsWith(McpSupport.Untrusted, text, StringComparison.Ordinal);
        Assert.Contains("1. Example A", text, StringComparison.Ordinal);
        Assert.Contains("https://example.org/a", text, StringComparison.Ordinal);
        Assert.Contains("About A", text, StringComparison.Ordinal);
// Claude Code shows the model the structured content, so the notice must be in it; the rest is the /v1/search body.
        var structured = System.Text.Json.Nodes.JsonNode.Parse(result.Structured().GetRawText())!.AsObject();
        Assert.Equal(McpSupport.Untrusted, (string?)structured["notice"]);
        structured.Remove("notice");
        Assert.True(
            System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(httpBody.GetRawText()), structured),
            "Apart from the notice, the structured content must equal the /v1/search body.");
    }

    [Fact]
    public async Task Search_arguments_reach_the_module_as_the_http_endpoint_sends_them()
    {
        await using var factory = Working();
        await using var client = await McpSupport.ConnectAsync(factory);

        await client.CallAsync("search", Args(("query", "cats")));
        await client.CallAsync("search", Args(
            ("query", "dogs"), ("categories", News), ("language", "en"), ("page", 2), ("timeRange", "month"),
            ("safeSearch", "strict"), ("engines", Brave), ("limit", 5)));

        var defaults = factory.SeenQueries[0];
        Assert.Equal(1, defaults.Page);
        Assert.Equal(10, defaults.Limit);
        var all = factory.SeenQueries[1];
        Assert.Equal("dogs", all.Text);
        Assert.Equal(["news"], all.Categories);
        Assert.Equal("en", all.Language);
        Assert.Equal(2, all.Page);
        Assert.Equal(SearchTimeRange.Month, all.TimeRange);
        Assert.Equal(SearchSafeSearch.Strict, all.SafeSearch);
        Assert.Equal(["brave"], all.Engines);
        Assert.Equal(5, all.Limit);
    }

    public static TheoryData<string, object, string> InvalidSearchArguments => new()
    {
        { "limit", 0, "limit" },
        { "limit", 51, "limit" },
        { "page", 11, "page" },
        { "timeRange", "week", "timeRange" },
        { "safeSearch", "none", "safeSearch" },
        { "engines", Enumerable.Repeat("e", 9).ToArray(), "engines" },
    };

    [Theory]
    [MemberData(nameof(InvalidSearchArguments))]
    public async Task The_contracts_limits_are_a_validation_error_that_never_reaches_the_module(string name, object value, string argument)
    {
        await using var factory = Working();
        await using var client = await McpSupport.ConnectAsync(factory);

        var result = await client.CallAsync("search", Args(("query", "cats"), (name, value)));

        Assert.Equal("urn:weblens:problem:validation", result.ErrorType());
        Assert.Contains("Argument: " + argument, result.Text(), StringComparison.Ordinal);
        Assert.Empty(factory.SeenQueries);
    }

    public static TheoryData<SearchErrorKind, string, int?> SearchErrorRows => new()
    {
        { SearchErrorKind.InvalidQuery, "validation", null },
        { SearchErrorKind.QueryUnsupported, "search-query-unsupported", null },
        { SearchErrorKind.NoEligibleInstance, "search-unavailable", 30 },
        { SearchErrorKind.AllAttemptsFailed, "search-failed", null },
        { SearchErrorKind.Timeout, "timeout", null },
    };

    /// <summary>Every search row of the error table as a tool result, including which rows say when to retry.</summary>
    [Theory]
    [MemberData(nameof(SearchErrorRows))]
    public async Task Each_search_error_kind_is_an_error_result_with_its_problem_type(SearchErrorKind kind, string type, int? retryAfter)
    {
        await using var factory = new WebLensFactory
        {
            SearchBehavior = (_, _) => throw new SearchException(kind, "Safe message.", [], TimeSpan.FromSeconds(30)) { Field = nameof(SearchQuery.Engines) },
        };
        await using var client = await McpSupport.ConnectAsync(factory);

        var result = await client.CallAsync("search", Args(("query", "cats")));

        Assert.Equal("urn:weblens:problem:" + type, result.ErrorType());
        Assert.Contains("Safe message.", result.Text(), StringComparison.Ordinal);
        Assert.Null(result.StructuredContent);
        Assert.Equal(retryAfter is null ? null : $"Retry after {retryAfter} second(s).", result.Text().Split('\n').FirstOrDefault(l => l.StartsWith("Retry after", StringComparison.Ordinal)));
        if (kind == SearchErrorKind.InvalidQuery)
        {
            Assert.Contains("Argument: engines", result.Text(), StringComparison.Ordinal);
        }
    }

    public static TheoryData<FetchErrorKind, string, int?> FetchErrorRows => new()
    {
        { FetchErrorKind.InvalidRequest, "validation", null },
        { FetchErrorKind.TargetNotAllowed, "target-not-allowed", null },
        { FetchErrorKind.TargetNotFound, "target-not-found", null },
        { FetchErrorKind.TargetAccessDenied, "target-access-denied", null },
        { FetchErrorKind.BotChallenge, "bot-challenge", null },
        { FetchErrorKind.CaptchaRequired, "captcha-required", null },
        { FetchErrorKind.UnsupportedContent, "unsupported-content", null },
        { FetchErrorKind.ContentNotFound, "content-not-found", null },
        { FetchErrorKind.TargetUnavailable, "target-unavailable", 7 },
        { FetchErrorKind.CapacityExceeded, "capacity-exceeded", 7 },
        { FetchErrorKind.BrowserUnavailable, "browser-unavailable", 7 },
        { FetchErrorKind.Timeout, "timeout", null },
        { FetchErrorKind.ConversionFailed, "internal", null },
    };

    /// <summary>Every fetch row of the error table as a tool result. A conversion failure is our bug: its detail stays out.</summary>
    [Theory]
    [MemberData(nameof(FetchErrorRows))]
    public async Task Each_fetch_error_kind_is_an_error_result_with_its_problem_type(FetchErrorKind kind, string type, int? retryAfter)
    {
        await using var factory = new WebLensFactory
        {
            FetchBehavior = (_, _) => throw new FetchException(kind, "Safe message.") { RetryAfter = TimeSpan.FromSeconds(7), Field = nameof(FetchRequest.ContentSelector) },
        };
        await using var client = await McpSupport.ConnectAsync(factory);

        var result = await client.CallAsync("fetch", Args(("url", "https://example.org/")));

        Assert.Equal("urn:weblens:problem:" + type, result.ErrorType());
        Assert.Equal(kind != FetchErrorKind.ConversionFailed, result.Text().Contains("Safe message.", StringComparison.Ordinal));
        Assert.Equal(retryAfter is null ? null : $"Retry after {retryAfter} second(s).", result.Text().Split('\n').FirstOrDefault(l => l.StartsWith("Retry after", StringComparison.Ordinal)));
        if (kind == FetchErrorKind.InvalidRequest)
        {
            Assert.Contains("Argument: contentSelector", result.Text(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_long_page_reads_end_to_end_in_parts_that_join_to_the_whole_page()
    {
        // 45,001 characters with a surrogate pair straddling the first part boundary.
        var markdown = new string('a', 19_999) + "\U0001F408" + new string('b', 25_000);
        await using var factory = Working(markdown);
        await using var client = await McpSupport.ConnectAsync(factory);

        var parts = new List<string>();
        var start = 0;
        string? fingerprint = null;
        while (true)
        {
            var result = await client.CallAsync("fetch", Args(("url", "https://example.org/"), ("start", start), ("fingerprint", fingerprint)));
            Assert.NotEqual(true, result.IsError);
            var text = result.Text();
            Assert.StartsWith(McpSupport.Untrusted, text, StringComparison.Ordinal);
            var body = text[(text.IndexOf("\n---\n", StringComparison.Ordinal) + 5)..];
            Assert.True(body.Length <= 20_000);
            parts.Add(body);

            var next = text.Split('\n').Single(l => l.StartsWith("More follows", StringComparison.Ordinal) || l == "This is the last part.");
            if (next == "This is the last part.")
            {
                break;
            }

            fingerprint = text.Split('\n').Single(l => l.StartsWith("Fingerprint: ", StringComparison.Ordinal))["Fingerprint: ".Length..];
            start = int.Parse(next.Split("start=")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal(markdown, string.Concat(parts));
        Assert.Equal(19_999, parts[0].Length);
        Assert.Equal(3, parts.Count);

        // The whole page every time, so every part is the same cache entry as a plain /v1/fetch.
        Assert.All(factory.SeenFetches, f => Assert.Null(f.MaxChars));
    }

    [Fact]
    public async Task A_part_says_where_it_is_and_whether_the_page_was_cut_at_the_servers_limit()
    {
        await using var factory = new WebLensFactory { FetchBehavior = (_, _) => Task.FromResult(Page(new string('x', 30), truncated: true)) };
        await using var client = await McpSupport.ConnectAsync(factory);

        var text = (await client.CallAsync("fetch", Args(("url", "https://example.org/"), ("maxPartChars", 10)))).Text();

        Assert.Contains("URL: https://example.org/final", text, StringComparison.Ordinal);
        Assert.Contains("Title: Title", text, StringComparison.Ordinal);
        Assert.Contains("Characters 0 to 10 of 30 (the page was cut at the server's limit).", text, StringComparison.Ordinal);
        Assert.Contains("More follows: call fetch with start=10 and fingerprint=", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_part_size_above_20000_is_lowered_to_20000()
    {
        await using var factory = Working(new string('x', 50_000));
        await using var client = await McpSupport.ConnectAsync(factory);

        var text = (await client.CallAsync("fetch", Args(("url", "https://example.org/"), ("maxPartChars", 40_000)))).Text();

        Assert.Contains("Characters 0 to 20000 of 50000.", text, StringComparison.Ordinal);
    }

    public static TheoryData<string, object, string> InvalidFetchArguments => new()
    {
        { "start", -1, "start" },
        { "maxPartChars", 0, "maxPartChars" },
        { "timeoutSeconds", 121, "timeoutSeconds" },
    };

    [Theory]
    [MemberData(nameof(InvalidFetchArguments))]
    public async Task Invalid_fetch_arguments_are_a_validation_error_that_never_reaches_the_module(string name, object value, string argument)
    {
        await using var factory = Working();
        await using var client = await McpSupport.ConnectAsync(factory);

        var result = await client.CallAsync("fetch", Args(("url", "https://example.org/"), (name, value)));

        Assert.Equal("urn:weblens:problem:validation", result.ErrorType());
        Assert.Contains("Argument: " + argument, result.Text(), StringComparison.Ordinal);
        Assert.Empty(factory.SeenFetches);
    }

    [Fact]
    public async Task A_start_past_the_end_is_a_validation_error_that_gives_the_pages_length()
    {
        await using var factory = Working(new string('x', 30));
        await using var client = await McpSupport.ConnectAsync(factory);

        var result = await client.CallAsync("fetch", Args(("url", "https://example.org/"), ("start", 30)));

        Assert.Equal("urn:weblens:problem:validation", result.ErrorType());
        Assert.Contains("the page has 30 characters", result.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_that_changed_between_parts_is_refused_rather_than_stitched()
    {
        var markdown = new string('x', 30);
        await using var factory = new WebLensFactory { FetchBehavior = (_, _) => Task.FromResult(Page(markdown)) };
        await using var client = await McpSupport.ConnectAsync(factory);
        var first = (await client.CallAsync("fetch", Args(("url", "https://example.org/"), ("maxPartChars", 10)))).Text();
        var fingerprint = first.Split('\n').Single(l => l.StartsWith("Fingerprint: ", StringComparison.Ordinal))["Fingerprint: ".Length..];

        markdown = new string('y', 30);
        var result = await client.CallAsync("fetch", Args(("url", "https://example.org/"), ("start", 10), ("fingerprint", fingerprint)));

        Assert.Equal("urn:weblens:problem:page-changed", result.ErrorType());
        Assert.Contains("start=0", result.Text(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_options_reach_the_module_as_the_http_endpoint_sends_them()
    {
        await using var factory = Working();
        await using var client = await McpSupport.ConnectAsync(factory);

        await client.CallAsync("fetch", Args(
            ("url", "https://example.org/a"), ("contentSelector", "main"), ("readySelector", "#ready"), ("excludeSelectors", Nav),
            ("includeLinks", false), ("includeImages", true), ("timeoutSeconds", 10), ("bypassCache", true)));

        var seen = Assert.Single(factory.SeenFetches);
        Assert.Equal("https://example.org/a", seen.Url);
        Assert.Equal("main", seen.ContentSelector);
        Assert.Equal("#ready", seen.ReadySelector);
        Assert.Equal(["nav"], seen.ExcludeSelectors);
        Assert.False(seen.IncludeLinks);
        Assert.True(seen.IncludeImages);
        Assert.Equal(TimeSpan.FromSeconds(10), seen.Timeout);
        Assert.True(seen.BypassCache);
        Assert.Null(seen.MaxChars);
    }

    [Fact]
    public async Task Cancelling_a_call_cancels_the_fetch_like_a_disconnected_http_client()
    {
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        await using var factory = new WebLensFactory
        {
            FetchBehavior = async (_, ct) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }

                return Page("never");
            },
        };
        await using var client = await McpSupport.ConnectAsync(factory);
        using var cts = new CancellationTokenSource();

        var call = client.CallUntilAsync("fetch", Args(("url", "https://example.org/")), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        // The client sees its own cancellation (the test server reports it as a failed request); what matters is the server side.
        Assert.NotNull(await Record.ExceptionAsync(() => call));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Both_key_forms_open_mcp()
    {
        await using var factory = Working();
        await using var header = await McpSupport.ConnectAsync(factory);
        await using var bearer = await McpSupport.ConnectAsync(factory, bearer: true);

        Assert.NotEqual(true, (await header.CallAsync("search", Args(("query", "cats")))).IsError);
        Assert.NotEqual(true, (await bearer.CallAsync("search", Args(("query", "cats")))).IsError);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("not-a-key", false)]
    [InlineData("not-a-key", true)]
    public async Task Without_a_valid_key_mcp_is_a_401_and_nothing_runs(string? key, bool bearer)
    {
        await using var factory = Working();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Remove("X-Api-Key");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"cats"}}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation(bearer ? "Authorization" : "X-Api-Key", bearer ? "Bearer " + key : key);
        }

        var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("urn:weblens:problem:unauthorized", body.GetProperty("type").GetString());
        Assert.Empty(factory.SeenQueries);
    }

    [Fact]
    public async Task The_bearer_form_is_not_accepted_on_v1()
    {
        await using var factory = Working();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Remove("X-Api-Key");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", WebLensFactory.AllScopesKey);

        var response = await http.PostAsync("/v1/search", new StringContent("""{ "query": "cats" }""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.SeenQueries);
    }

    [Fact]
    public async Task A_key_without_the_fetch_scope_gets_no_fetch_result()
    {
        await using var factory = Working();
        await using var client = await McpSupport.ConnectAsync(factory, WebLensFactory.SearchOnlyKey);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var call = await Record.ExceptionAsync(() => client.CallAsync("fetch", Args(("url", "https://example.org/"))));

        Assert.Equal(["search"], tools.Select(t => t.Name));
        Assert.IsType<McpException>(call, exactMatch: false);
        Assert.Empty(factory.SeenFetches);
        Assert.NotEqual(true, (await client.CallAsync("search", Args(("query", "cats")))).IsError);
    }

    [Fact]
    public async Task Mcp_and_v1_spend_from_one_budget_per_key()
    {
        await using var factory = Working();
        factory.Settings["WebLens:Api:RateLimitProfiles:default:SearchBurst"] = "2";
        factory.Settings["WebLens:Api:RateLimitProfiles:default:SearchPerMinute"] = "1";
        await using var client = await McpSupport.ConnectAsync(factory);
        using var http = factory.CreateClient();
        Task<HttpResponseMessage> PostSearch() =>
            http.PostAsync("/v1/search", new StringContent("""{ "query": "cats" }""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        var first = await PostSearch();
        var second = await client.CallAsync("search", Args(("query", "cats")));
        var third = await client.CallAsync("search", Args(("query", "cats")));
        var fourth = await PostSearch();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotEqual(true, second.IsError);
        Assert.Equal("urn:weblens:problem:rate-limited", third.ErrorType());
        Assert.Matches(@"Retry after \d+ second\(s\)\.", third.Text());
        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        Assert.Equal(2, factory.SeenQueries.Count);
    }

    [Fact]
    public async Task A_tool_call_answered_from_the_cache_costs_no_permit()
    {
        await using var factory = Working();
        factory.SearchCache = q => q.Text == "cached" ? Samples.Response("cached") : null;
        factory.Settings["WebLens:Api:RateLimitProfiles:default:SearchBurst"] = "1";
        factory.Settings["WebLens:Api:RateLimitProfiles:default:SearchPerMinute"] = "1";
        await using var client = await McpSupport.ConnectAsync(factory);

        var miss = await client.CallAsync("search", Args(("query", "cats")));
        var hit = await client.CallAsync("search", Args(("query", "cached")));
        var refused = await client.CallAsync("search", Args(("query", "dogs")));

        Assert.NotEqual(true, miss.IsError);
        Assert.NotEqual(true, hit.IsError);
        Assert.Equal("urn:weblens:problem:rate-limited", refused.ErrorType());
        Assert.Single(factory.SeenQueries);
    }

    [Fact]
    public async Task A_fetch_call_holds_its_concurrency_permit_until_it_finishes()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        await using var factory = new WebLensFactory
        {
            FetchBehavior = async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return Page("# Title\n");
            },
        };
        factory.Settings["WebLens:Api:RateLimitProfiles:default:FetchConcurrency"] = "1";
        await using var client = await McpSupport.ConnectAsync(factory);
        using var http = factory.CreateClient();

        var running = client.CallAsync("fetch", Args(("url", "https://example.org/")));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var viaHttp = await http.PostAsync("/v1/fetch", new StringContent("""{ "url": "https://example.org/" }""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        release.SetResult();

        Assert.NotEqual(true, (await running).IsError);
        Assert.Equal(HttpStatusCode.TooManyRequests, viaHttp.StatusCode);
    }
}
