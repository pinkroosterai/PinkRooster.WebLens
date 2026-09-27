using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace PinkRooster.WebLens.Fetch;

/// <summary>What one navigation produced: the main document's response and the rendered DOM.</summary>
internal sealed record RenderedPage(
    Uri FinalUrl,
    int? Status,
    IReadOnlyDictionary<string, string> Headers,
    string? ContentType,
    string Html,
    int HtmlBytes,
    bool ReadinessTimedOut,
    long RenderMs,
    long NavigationMs = 0,
    long ReadinessMs = 0)
{
    public bool IsHtml => IsHtmlType(ContentType);

    /// <summary>Only HTML proceeds. No content type at all is treated as HTML, as browsers do.</summary>
    public static bool IsHtmlType(string? contentType) => contentType is null
        || contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Navigates to <c>DOMContentLoaded</c>, then runs the readiness strategy. Never <c>networkidle</c>.</summary>
internal sealed class PageRenderer(IOptions<FetchOptions> options, ReadinessStrategy readiness, UrlGuard guard, TimeProvider time)
{
    private readonly FetchOptions _options = options.Value;

    public async Task<RenderedPage> RenderAsync(FetchContext context, NormalizedFetch fetch, TimeSpan remaining, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var page = context.Page;
        var navigationTimeout = Min(_options.Navigation.NavigationTimeout, remaining);

        // GotoAsync does not throw for HTTP error statuses; the main response is always inspected.
        var response = await PlaywrightCalls.WaitAsync(
            page.GotoAsync(fetch.Url.AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = Ms(navigationTimeout) }),
            ct);

        await CheckRedirectChainAsync(response, fetch.Url, new Uri(page.Url), ct);

        var headers = response is null ? new Dictionary<string, string>() : await PlaywrightCalls.WaitAsync(response.AllHeadersAsync(), ct);
        headers.TryGetValue("content-type", out var contentType);
        var status = response?.Status;

        var navigationMs = (long)time.GetElapsedTime(started).TotalMilliseconds;
        var readinessMs = 0L;
        var readinessTimedOut = false;
        var html = "";
        if (RenderedPage.IsHtmlType(contentType))
        {
            // Only a successful page is worth waiting for; an error or challenge page is classified as it arrived.
            if (status is null or (>= 200 and < 300))
            {
                var left = remaining - time.GetElapsedTime(started);
                var readinessStarted = time.GetTimestamp();
                readinessTimedOut = !await readiness.WaitAsync(page, context.DataRequests, fetch.ReadySelector, Min(_options.Navigation.ReadinessTimeout, left), ct);
                readinessMs = (long)time.GetElapsedTime(readinessStarted).TotalMilliseconds;
            }

            html = await PlaywrightCalls.WaitAsync(page.ContentAsync(), ct);
        }

        var bytes = Encoding.UTF8.GetByteCount(html);
        if (bytes > _options.Limits.MaxRenderedHtmlBytes)
        {
            throw new FetchException(FetchErrorKind.UnsupportedContent, "The rendered page is larger than this service accepts.") { TargetStatus = status };
        }

        return new RenderedPage(
            new Uri(page.Url),
            status,
            headers,
            contentType,
            html,
            bytes,
            readinessTimedOut,
            (long)time.GetElapsedTime(started).TotalMilliseconds,
            navigationMs,
            readinessMs);
    }

    /// <summary>
    /// The route guard never sees redirect hops (Playwright does not route them), so every hop of the main document is
    /// checked here, and a final host other than the requested one gets the full check with DNS. A denied hop means the
    /// page is refused: nothing it returned is used. Stopping the hop's request itself is the egress proxy's job.
    /// </summary>
    private async Task CheckRedirectChainAsync(IResponse? response, Uri requested, Uri final, CancellationToken ct)
    {
        for (var request = response?.Request; request is not null; request = request.RedirectedFrom)
        {
            if (Uri.TryCreate(request.Url, UriKind.Absolute, out var hop) && guard.CheckWithoutDns(hop) is { } denial)
            {
                throw UrlGuard.NotAllowed(denial);
            }
        }

        if (!string.Equals(final.IdnHost, requested.IdnHost, StringComparison.OrdinalIgnoreCase) && final.Scheme is "http" or "https")
        {
            await guard.CheckAsync(final, ct);
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static float Ms(TimeSpan span) => (float)Math.Max(1, span.TotalMilliseconds);
}
