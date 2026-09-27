using System.Net;
using System.Text;
using PinkRooster.WebLens.Search;

namespace PinkRooster.WebLens.Api.Tests;

internal static class Samples
{
    public static SearchResponse Response(string query = "cats") => new(
        query,
        [new SearchResult("https://example.org/a", "Example A", "About A", "general", ["brave", "duckduckgo"], 1.5, new DateTimeOffset(2026, 9, 5, 10, 34, 41, TimeSpan.Zero), null, "https://example.org/a.png")],
        [new SearchAnswer("42")],
        ["cat food"],
        ["cats"],
        [new SearchInfobox("Cat", "A small mammal", "https://example.org/cat")],
        new SearchMeta(1, 1, true, [new SearchEngineFailure("brave", "timeout")], 2, 12, false));

    public const string SearxJson = """
        { "query": "cats",
          "results": [ { "url": "https://example.org/a", "title": "Example A", "content": "About A", "engines": ["brave"], "category": "general", "score": 1.0 } ],
          "answers": [], "corrections": [], "suggestions": [], "infoboxes": [], "unresponsive_engines": [] }
        """;

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>A network stand-in that answers every request with one delegate.</summary>
internal sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _paths = new();

    public int Calls => _paths.Count;

    public int CallsTo(string path) => _paths.Count(p => p == path);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _paths.Enqueue(request.RequestUri!.AbsolutePath);
        return respond(request, cancellationToken);
    }
}
