using System.Net;
using System.Text;
using System.Text.Json;
using PinkRooster.WebLens.Fetch;

namespace PinkRooster.WebLens.Api.Tests;

public class FetchEndpointTests
{
    private static async Task<(HttpResponseMessage Response, JsonElement Body)> PostAsync(WebLensFactory factory, string json)
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsync("/v1/fetch", new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static FetchResult Result() => new(
        new Uri("https://example.org/a"),
        new Uri("https://example.org/b"),
        "Title",
        "# Title\n\nBody.\n",
        false,
        new Fetch.PageMetadata("Mara", "en", new DateTimeOffset(2026, 8, 14, 9, 30, 0, TimeSpan.Zero), "Site", "Excerpt"),
        new Fetch.FetchDiagnostics(200, "smartreader", 0.95, 1, false, 12_345, 16, 800, 20, 5, false, 600, 200, "browser"));

    private static WebLensFactory Returning() => new() { FetchBehavior = (_, _) => Task.FromResult(Result()) };

    private static WebLensFactory Throwing(Exception exception) => new() { FetchBehavior = (_, _) => throw exception };

    [Fact]
    public async Task Returns_the_documented_camel_case_shape()
    {
        await using var factory = Returning();

        var (response, body) = await PostAsync(factory, """{ "url": "https://example.org/a" }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://example.org/a", body.GetProperty("requestedUrl").GetString());
        Assert.Equal("https://example.org/b", body.GetProperty("finalUrl").GetString());
        Assert.Equal("# Title\n\nBody.\n", body.GetProperty("markdown").GetString());
        Assert.False(body.GetProperty("truncated").GetBoolean());
        Assert.Equal("Mara", body.GetProperty("metadata").GetProperty("author").GetString());
        var diagnostics = body.GetProperty("diagnostics");
        Assert.Equal("smartreader", diagnostics.GetProperty("extractionStrategy").GetString());
        Assert.Equal(0.95, diagnostics.GetProperty("qualityScore").GetDouble());
        Assert.Equal(200, diagnostics.GetProperty("targetStatus").GetInt32());
        Assert.False(diagnostics.GetProperty("cached").GetBoolean());
        Assert.Equal(800, diagnostics.GetProperty("renderMs").GetInt64());
        Assert.Equal(600, diagnostics.GetProperty("navigationMs").GetInt64());
        Assert.Equal(200, diagnostics.GetProperty("readinessMs").GetInt64());
        Assert.Equal("browser", diagnostics.GetProperty("renderer").GetString());
    }

    [Fact]
    public async Task Translates_the_request_with_defaults_and_options()
    {
        await using var factory = Returning();

        await PostAsync(factory, """{ "url": "https://example.org/a" }""");
        await PostAsync(factory, """
            { "url": "https://example.org/a", "options": { "contentSelector": "main", "readySelector": "#ready", "excludeSelectors": ["nav"],
              "includeLinks": false, "includeImages": true, "maxChars": 5000, "timeoutSeconds": 10, "bypassCache": true } }
            """);

        var defaults = factory.SeenFetches[0];
        Assert.True(defaults.IncludeLinks);
        Assert.False(defaults.IncludeImages);
        Assert.Null(defaults.Timeout);

        var options = factory.SeenFetches[1];
        Assert.Equal("main", options.ContentSelector);
        Assert.Equal("#ready", options.ReadySelector);
        Assert.Equal(["nav"], options.ExcludeSelectors);
        Assert.False(options.IncludeLinks);
        Assert.True(options.IncludeImages);
        Assert.Equal(5000, options.MaxChars);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Timeout);
    }

    public static TheoryData<string, string> InvalidBodies => new()
    {
        { """{}""", "Url" },
        { """{ "url": "" }""", "Url" },
        { """{ "url": "https://example.org/", "options": { "maxChars": 10 } }""", "Options.MaxChars" },
        { """{ "url": "https://example.org/", "options": { "timeoutSeconds": 0 } }""", "Options.TimeoutSeconds" },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Contract_limits_are_a_400_validation_problem_and_never_reach_the_module(string json, string field)
    {
        await using var factory = Returning();

        var (response, body) = await PostAsync(factory, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("urn:weblens:problem:validation", body.GetProperty("type").GetString());
        Assert.Contains(body.GetProperty("errors").EnumerateObject(), e => e.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(factory.SeenFetches);
    }

    public static TheoryData<string, string> BodiesTheModuleRejects => new()
    {
        { """{ "url": "ftp://example.org/file" }""", "Url" },
        { """{ "url": "not a url" }""", "Url" },
        { """{ "url": "https://example.org/", "options": { "contentSelector": "div[" } }""", "Options.ContentSelector" },
        { """{ "url": "https://example.org/", "options": { "excludeSelectors": ["a","b","c","d","e","f","g","h","i","j","k"] } }""", "Options.ExcludeSelectors" },
    };

    [Theory]
    [MemberData(nameof(BodiesTheModuleRejects))]
    public async Task Url_and_selector_rules_are_the_modules_and_come_back_as_the_same_validation_problem(string json, string field)
    {
        // The real module: validation happens before any browser work, so none is launched.
        await using var factory = new WebLensFactory();

        var (response, body) = await PostAsync(factory, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("urn:weblens:problem:validation", body.GetProperty("type").GetString());
        Assert.Equal("InvalidRequest", body.GetProperty("errorKind").GetString());
        Assert.Equal(field, Assert.Single(body.GetProperty("errors").EnumerateObject()).Name);
    }

    public static TheoryData<FetchErrorKind, int, string, int?> ErrorRows => new()
    {
        { FetchErrorKind.TargetNotAllowed, 422, "target-not-allowed", null },
        { FetchErrorKind.TargetNotFound, 422, "target-not-found", null },
        { FetchErrorKind.TargetAccessDenied, 422, "target-access-denied", null },
        { FetchErrorKind.BotChallenge, 422, "bot-challenge", null },
        { FetchErrorKind.CaptchaRequired, 422, "captcha-required", null },
        { FetchErrorKind.UnsupportedContent, 422, "unsupported-content", null },
        { FetchErrorKind.ContentNotFound, 422, "content-not-found", null },
        { FetchErrorKind.TargetUnavailable, 502, "target-unavailable", 7 },
        { FetchErrorKind.CapacityExceeded, 503, "capacity-exceeded", 2 },
        { FetchErrorKind.BrowserUnavailable, 503, "browser-unavailable", 5 },
        { FetchErrorKind.Timeout, 504, "timeout", null },
    };

    /// <summary>Every fetch row of the error table, including which rows carry Retry-After.</summary>
    [Theory]
    [MemberData(nameof(ErrorRows))]
    public async Task Each_fetch_error_kind_maps_to_its_row(FetchErrorKind kind, int status, string type, int? retryAfter)
    {
        await using var factory = Throwing(new FetchException(kind, "Safe message.") { TargetStatus = 403, RetryAfter = TimeSpan.FromSeconds(retryAfter ?? 9) });

        var (response, body) = await PostAsync(factory, """{ "url": "https://example.org/" }""");

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("urn:weblens:problem:" + type, body.GetProperty("type").GetString());
        Assert.Equal(kind.ToString(), body.GetProperty("errorKind").GetString());
        Assert.Equal("Safe message.", body.GetProperty("detail").GetString());
        Assert.Equal(403, body.GetProperty("targetStatus").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        if (retryAfter is { } seconds)
        {
            Assert.Equal(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(response.Headers.GetValues("Retry-After")));
            Assert.Equal(seconds, body.GetProperty("retryAfterSeconds").GetInt32());
        }
        else
        {
            Assert.False(response.Headers.Contains("Retry-After"));
            Assert.Equal(JsonValueKind.Null, body.GetProperty("retryAfterSeconds").ValueKind);
        }
    }

    [Fact]
    public async Task A_production_host_without_an_egress_proxy_refuses_to_start()
    {
        var ex = await HostStart.FailureAsync("Production", []);

        Assert.Contains("RequireEgressProxy is set but no EgressProxy:Server is configured", ex.ToString());
    }

    [Fact]
    public async Task A_production_host_refuses_the_development_relaxations()
    {
        var ex = await HostStart.FailureAsync("Production", new()
        {
            ["WebLens:Fetch:Security:AllowPrivateNetworks"] = "true",
            ["WebLens:Fetch:Security:RequireEgressProxy"] = "false",
        });

        Assert.Contains("AllowPrivateNetworks may be true only in Development or Testing", ex.ToString());
        Assert.Contains("RequireEgressProxy may be false only in Development or Testing", ex.ToString());
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://user:pass@example.org/")]
    [InlineData("http://example.org:22/")]
    public async Task A_url_the_guard_refuses_is_a_422_target_not_allowed_that_names_no_address(string url)
    {
        // The real module, in Production with a proxy configured, so the private-network rules are on.
        await using var factory = new WebLensFactory { Environment = "Production" };

        var (response, body) = await PostAsync(factory, $$"""{ "url": "{{url}}" }""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("urn:weblens:problem:target-not-allowed", body.GetProperty("type").GetString());
        Assert.DoesNotContain("127.0.0.1", body.GetProperty("detail").GetString());
        Assert.DoesNotContain("169.254", body.GetRawText());
    }

    [Fact]
    public async Task A_conversion_failure_is_a_500_internal_problem_without_its_detail()
    {
        await using var factory = Throwing(new FetchException(FetchErrorKind.ConversionFailed, "The page could not be converted.", new InvalidOperationException("stack detail")));

        var (response, body) = await PostAsync(factory, """{ "url": "https://example.org/" }""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("urn:weblens:problem:internal", body.GetProperty("type").GetString());
        Assert.DoesNotContain("stack detail", body.GetRawText());
    }
}
