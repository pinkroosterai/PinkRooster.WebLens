using System.Net;
using System.Text;

namespace PinkRooster.WebLens.Search.Tests;

internal sealed record RecordedRequest(string Host, string Path, HttpMethod Method, string Body);

/// <summary>Stands in for the network: one handler per host name, 404 for anything unknown. Records every request.</summary>
internal sealed class FakeSearxServer : HttpMessageHandler
{
    public delegate Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken ct);

    private readonly Dictionary<string, Handler> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RecordedRequest> _requests = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_gate) { return [.. _requests]; } }
    }

    public IReadOnlyList<RecordedRequest> SearchRequestsTo(string host) =>
        [.. Requests.Where(r => r.Host == host && r.Path == "/search")];

    public FakeSearxServer On(string host, Handler handler)
    {
        _hosts[host] = handler;
        return this;
    }

    /// <summary>Answers /search with <paramref name="search"/> and /config with the recorded SearXNG config.</summary>
    public FakeSearxServer OnSearxng(string host, Func<HttpRequestMessage, HttpResponseMessage> search) =>
        On(host, (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/config" ? Json(Fixture.Read(Fixture.Config)) : search(request)));

    public FakeSearxServer OnSearxngJson(string host, string json) => OnSearxng(host, _ => Json(json));

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("", Encoding.UTF8, "text/plain") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate)
        {
            _requests.Add(new RecordedRequest(request.RequestUri!.Host, request.RequestUri.AbsolutePath, request.Method, body));
        }

        return _hosts.TryGetValue(request.RequestUri!.Host, out var handler)
            ? await handler(request, cancellationToken)
            : Status(HttpStatusCode.NotFound);
    }
}
