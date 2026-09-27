using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PinkRooster.WebLens.Fetch.BrowserTests;

/// <summary>
/// The local fixture site: Kestrel on a loopback port, one route per behaviour a fetch must handle.
/// Counts every request by path, so tests can see what the browser did and did not load.
/// </summary>
internal sealed class FixtureSite : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, int> _hits = new(StringComparer.Ordinal);

    private FixtureSite(WebApplication app) => _app = app;

    public Uri BaseUri { get; private set; } = null!;

    public string Url(string path) => new Uri(BaseUri, path).AbsoluteUri;

    public int Hits(string path) => _hits.GetValueOrDefault(path);

    public int Port => BaseUri.Port;

    /// <param name="address">Loopback by default. 127.0.0.2 serves as a "private target" the guard is configured to refuse.</param>
    public static async Task<FixtureSite> StartAsync(string address = "127.0.0.1")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{address}:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var site = new FixtureSite(app);

        app.Use(async (context, next) =>
        {
            site._hits.AddOrUpdate(context.Request.Path.Value ?? "/", 1, (_, n) => n + 1);
            await next(context);
        });

        app.MapGet("/article", () => Html(Article("Saltmere tram line opens", "")));
        app.MapGet("/redirect", () => Results.Redirect("/article"));
        app.MapGet("/long", () => Html(Article("A very long report", string.Concat(Enumerable.Range(1, 400).Select(i =>
            $"<p>Section {i}: the committee heard evidence about the tram line, the ferry square and the hills, and wrote it all down at length.</p>")))));
        app.MapGet("/status/{code:int}", (int code) => Html($"<html><head><title>Status {code}</title></head><body><h1>Status {code}</h1></body></html>", code));
        app.MapGet("/always-503", () => Html("<html><body><h1>Down for maintenance</h1></body></html>", 503));
        app.MapGet("/flaky", () => site.Hits("/flaky") == 1
            ? Html("<html><body><h1>Try again</h1></body></html>", 503)
            : Html(Article("Recovered after a retry", "")));
        // An app shell that loads its content with fetch, as real apps do: a short page with a data request in flight.
        app.MapGet("/spa", () => Html("""
            <html><head><title>App</title></head><body><div id="app">Loading…</div>
            <script>
              fetch('/spa-data').then(r => r.json()).then(paragraphs => {
                document.getElementById('app').innerHTML = '<article><h1>Rendered by script</h1>' +
                  paragraphs.map(p => '<p>' + p + '</p>').join('') + '</article>';
              });
            </script></body></html>
            """));
        app.MapGet("/spa-data", async () =>
        {
            await Task.Delay(800);
            return Results.Json(Enumerable.Range(0, 8).Select(i => $"Client-side paragraph {i} with enough words to count as real content for the extractor."));
        });

        // The same shell, but its code arrives late (a code-split chunk): a script download in flight.
        app.MapGet("/spa-chunk", () => Html("""
            <html><head><title>App</title></head><body><div id="app">Loading…</div>
            <script>const s = document.createElement('script'); s.src = '/chunk.js'; document.body.appendChild(s);</script></body></html>
            """));
        app.MapGet("/chunk.js", async () =>
        {
            await Task.Delay(800);
            return Results.Text("""
                document.getElementById('app').innerHTML = '<article><h1>Rendered by a late chunk</h1>' +
                  Array.from({ length: 8 }, (_, i) => '<p>Chunk paragraph ' + i + ' with enough words to count as real content for the extractor.</p>').join('') +
                  '</article>';
                """, "text/javascript");
        });

        // Short as a whole, like example.com: nothing will ever add to it.
        app.MapGet("/short", () => Html("""
            <html><head><title>Placeholder Domain</title></head><body><div><h1>Placeholder Domain</h1>
            <p>This name is kept for use in examples and manuals; it needs no permission to cite.</p>
            <p><a href="/about">Read more</a></p></div></body></html>
            """));

        // Static HTML whose main text asks for JavaScript; script replaces it with the real article.
        app.MapGet("/needs-js", () => Html("""
            <html><head><title>Needs script</title></head><body><main id="app">
            <p>Please enable JavaScript to use this site. This page shows its stories, timetables and notices only once
            JavaScript is turned on in your browser, because everything on it is loaded and drawn by script.</p>
            <p>If you are seeing this message, your browser has JavaScript switched off or it failed to load.</p></main>
            <script>
              document.getElementById('app').innerHTML = '<article><h1>Drawn by script</h1>' +
                Array.from({ length: 6 }, (_, i) => '<p>Scripted story ' + i + ' with enough words to count as real content for the extractor.</p>').join('') +
                '</article>';
            </script></body></html>
            """));

        // Challenges plain HTTP clients (by user agent) but serves browsers the article.
        app.MapGet("/challenge-plain", (HttpContext context) =>
        {
            if (!context.Request.Headers.UserAgent.ToString().Contains("WebLens", StringComparison.Ordinal))
            {
                return Html(Article("Served to browsers", ""));
            }

            context.Response.Headers["cf-mitigated"] = "challenge";
            return Html("""
                <html><head><title>Just a moment...</title></head><body>
                <p>Checking if the site connection is secure</p><form id="challenge-form" action="/" method="POST"></form></body></html>
                """, 403);
        });

        // A carousel: toggles a class forever, but its content never changes.
        app.MapGet("/carousel", () => Html(Article("Page with a carousel", """
            <div id="slides"><p class="slide">Slide one</p><p class="slide">Slide two</p></div>
            <script>setInterval(() => document.querySelectorAll('.slide').forEach(s => s.classList.toggle('active')), 100);</script>
            """)));

        // A ticker: adds content forever, so the page never goes quiet.
        app.MapGet("/ticker", () => Html(Article("Page with a live ticker", """
            <ul id="ticker"></ul>
            <script>setInterval(() => { const li = document.createElement('li'); li.textContent = 'Tick ' + Date.now(); document.getElementById('ticker').appendChild(li); }, 100);</script>
            """)));
        app.MapGet("/late", () => Html(Article("Early text", """
            <div id="late"></div>
            <script>setTimeout(() => { document.getElementById('late').innerHTML = '<p class="done">Arrived late but awaited.</p>'; }, 1500);</script>
            """)));
        app.MapGet("/popup", () => Html(Article("Page that opens things", """
            <script>
              window.open('/article', '_blank');
              confirm('Leave?');
              alert('Hello');
            </script>
            """)));
        app.MapGet("/media", () => Html(Article("Page with media", """
            <img src="/image.png" alt="A picture">
            <video src="/clip.mp4" autoplay muted></video>
            <audio src="/sound.mp3" autoplay></audio>
            """)));
        app.MapGet("/image.png", () => Results.Bytes(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="), "image/png"));
        app.MapGet("/clip.mp4", () => Results.Bytes(new byte[1024], "video/mp4"));
        app.MapGet("/sound.mp3", () => Results.Bytes(new byte[1024], "audio/mpeg"));
        app.MapGet("/challenge", (HttpContext context) =>
        {
            context.Response.Headers["cf-mitigated"] = "challenge";
            return Html("""
                <html><head><title>Just a moment...</title></head><body>
                <p>Checking if the site connection is secure</p><form id="challenge-form" action="/" method="POST"></form>
                <script src="/cdn-cgi/challenge-platform/h/b/orchestrate/chl_page/v1"></script></body></html>
                """, 403);
        });
        app.MapGet("/pdf", () => Results.Bytes(Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n"), "application/pdf"));
        app.MapGet("/download", (HttpContext context) =>
        {
            context.Response.Headers.ContentDisposition = "attachment; filename=\"data.csv\"";
            return Results.Text("a,b\n1,2\n", "text/csv");
        });
        app.MapGet("/hang", async (HttpContext context) =>
        {
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
            return Results.Ok();
        });
        app.MapGet("/slow", async (HttpContext context) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted);
            return Html(Article("Slow but fine", ""));
        });
        // SSRF probes: a redirect to, and a page reaching out to, a URL the test chooses.
        app.MapGet("/redirect-to", (string url) => Results.Redirect(url));
        app.MapGet("/reaches-out", (string url) =>
        {
            var ws = "ws" + url[4..];
            return Html(Article("Page that reaches out", $$"""
                <img src="{{url}}/img.png"><script src="{{url}}/s.js"></script><iframe src="{{url}}/frame"></iframe>
                <script>fetch('{{url}}/xhr').catch(() => {}); try { new WebSocket('{{ws}}/ws'); } catch (e) {}</script>
                """));
        });
        app.MapGet("/health/details", () => Results.Text("SECRET-HEALTH-DETAILS", "text/plain"));

        app.MapGet("/hidden", () => Html(Article("Visible story", """
            <div style="display:none">SECRET-HIDDEN-TEXT</div><p hidden>SECRET-HIDDEN-TEXT</p>
            """)));

        await app.StartAsync();
        site.BaseUri = new Uri(app.Urls.Single());
        return site;
    }

    /// <summary>Requests of any kind this site received.</summary>
    public int TotalHits => _hits.Values.Sum();

    public string Summary()
    {
        return string.Join(", ", _hits.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static IResult Html(string html, int status = 200) => Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);

    /// <summary>A plain article with enough text to pass readiness and quality checks, plus site chrome and a relative link.</summary>
    private static string Article(string title, string extra) => $"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>{title}</title></head><body>
        <header><nav><a href="/">Home</a> <a href="/news">News</a> <a href="/sport">Sport</a></nav></header>
        <main><article><h1>{title}</h1>
        {string.Concat(Enumerable.Range(1, 6).Select(i => $"<p>Paragraph {i}: the borough's new line carries passengers from the ferry square to the northern hills, and residents have opinions about every stop along the way.</p>"))}
        <p>Read the <a href="/guide/timetable">timetable guide</a> for details.</p>
        {extra}
        </article></main>
        <footer><a href="/about">About</a> <a href="/contact">Contact</a></footer>
        </body></html>
        """;
}
