using System.Net;
using System.Text;

namespace PinkRooster.WebLens.Api.Tests;

/// <summary>
/// Search text, target URLs and their query strings, request bodies and API keys are never logged. A
/// marker goes through both endpoints and both MCP tools, on success and failure paths, with every log line captured at Trace level.
/// </summary>
public class SensitiveDataTests
{
    private const string Marker = "S3NSITIVE-MARKER-7f3a";

    [Fact]
    public async Task The_marker_appears_in_no_log_line()
    {
        var logs = new CapturedLogs();
        var searxStatus = HttpStatusCode.OK;
        await using var factory = new WebLensFactory
        {
            Logs = logs,
            Network = new DelegateHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsolutePath == "/config" ? Samples.Json("{}", HttpStatusCode.NotFound) : Samples.Json(Samples.SearxJson, searxStatus))),
        };
        using var client = factory.CreateClient();

        async Task<HttpStatusCode> Post(string path, string json, string? key = WebLensFactory.AllScopesKey)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Remove("X-Api-Key");
            if (key is not null)
            {
                request.Headers.TryAddWithoutValidation("X-Api-Key", key);
            }

            client.DefaultRequestHeaders.Remove("X-Api-Key");
            return (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
        }

        var outcomes = new List<HttpStatusCode>
        {
            // Search: a success, an upstream failure, a validation failure.
            await Post("/v1/search", $$"""{ "query": "cats {{Marker}}" }"""),
            await Post("/v1/search", $$"""{ "query": "cats {{Marker}}", "engines": ["{{Marker}}!"] }"""),
        };
        searxStatus = HttpStatusCode.InternalServerError;
        outcomes.Add(await Post("/v1/search", $$"""{ "query": "dogs {{Marker}}" }"""));

        // Fetch: refused by the URL guard, unresolvable, invalid selector. The marker is in the path and query only:
        // the origin host is allowed in logs.
        outcomes.Add(await Post("/v1/fetch", $$"""{ "url": "http://127.0.0.1/{{Marker}}?token={{Marker}}" }"""));
        outcomes.Add(await Post("/v1/fetch", $$"""{ "url": "https://example.invalid/{{Marker}}?token={{Marker}}" }"""));
        outcomes.Add(await Post("/v1/fetch", $$"""{ "url": "https://example.org/", "options": { "contentSelector": "div[{{Marker}}" } }"""));

        // A key that is the marker.
        outcomes.Add(await Post("/v1/search", """{ "query": "cats" }""", Marker));

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.BadGateway, (HttpStatusCode)422, HttpStatusCode.BadGateway, HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized],
            outcomes);
        // The same through MCP: a search, a refused fetch, a validation failure, and a bearer key that is the marker.
        searxStatus = HttpStatusCode.OK;
        await using (var mcp = await McpSupport.ConnectAsync(factory))
        {
            Assert.NotEqual(true, (await mcp.CallAsync("search", new() { ["query"] = $"cats {Marker}" })).IsError);
            Assert.True((await mcp.CallAsync("fetch", new() { ["url"] = $"http://127.0.0.1/{Marker}?token={Marker}" })).IsError);
            Assert.True((await mcp.CallAsync("fetch", new() { ["url"] = "https://example.org/", ["contentSelector"] = $"div[{Marker}" })).IsError);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => McpSupport.ConnectAsync(factory, Marker, bearer: true));

        Assert.NotEmpty(logs.Lines);
        var leaks = logs.Lines.Where(l => l.Message.Contains(Marker, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(leaks.Count == 0, "Logged: " + string.Join(" | ", leaks.Select(l => l.Message)));
        Assert.Contains(logs.Lines, l => l.Message.Contains("/v1/search -> 200", StringComparison.Ordinal));
        Assert.Contains(logs.Lines, l => l.Message.Contains("/mcp", StringComparison.Ordinal) && l.Level == Microsoft.Extensions.Logging.LogLevel.Information);
    }
}
