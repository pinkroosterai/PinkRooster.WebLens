using AngleSharp.Html.Dom;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace PinkRooster.WebLens.Fetch;

/// <summary>
/// One logical fetch under one overall deadline: origin circuits, then permits (origin first, then global),
/// then up to <c>NavigationAttempts</c> navigations, each on a fresh context, then extraction and conversion.
/// </summary>
internal sealed partial class WebFetchService(
    IOptions<FetchOptions> options,
    BrowserHost browsers,
    ContextFactory contexts,
    PageRenderer renderer,
    HttpPageLoader http,
    ResponseClassifier classifier,
    ContentExtractor extractor,
    MarkdownConverter converter,
    OriginRegistry origins,
    CapacityGate capacity,
    UrlGuard guard,
    TimeProvider time,
    ILogger<WebFetchService> logger) : IWebFetchService
{
    private readonly FetchOptions _options = options.Value;

    /// <summary>No cache here: the answer is always none, after validating.</summary>
    public Task<FetchResult?> TryGetCachedAsync(FetchRequest request, CancellationToken ct)
    {
        FetchRequestNormalizer.Normalize(request, _options);
        return Task.FromResult<FetchResult?>(null);
    }

    public async Task<FetchResult> FetchMarkdownAsync(FetchRequest request, CancellationToken ct)
    {
        var fetch = FetchRequestNormalizer.Normalize(request, _options);
        var started = time.GetTimestamp();

        using var budget = new CancellationTokenSource(fetch.Budget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        var run = new FetchRun(fetch, started);
        try
        {
            var result = await RunAsync(run, linked.Token);
            LogFetched(fetch.Url.Host, result.Diagnostics.ExtractionStrategy, run.Attempts, result.Diagnostics.RenderMs, result.Diagnostics.ExtractMs, result.Diagnostics.ConvertMs, result.Diagnostics.MarkdownChars);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
        {
            LogFailed(fetch.Url.Host, FetchErrorKind.Timeout, run.Attempts);
            throw new FetchException(FetchErrorKind.Timeout, "The fetch did not finish within its time budget.") { Attempts = run.Attempts };
        }
        catch (FetchException ex)
        {
            if (ex.DenialReason is { } reason)
            {
                LogNotAllowed(fetch.Url.Host, reason);
            }

            LogFailed(fetch.Url.Host, ex.Kind, run.Attempts);
            throw;
        }
    }

    private async Task<FetchResult> RunAsync(FetchRun run, CancellationToken ct)
    {
        var fetch = run.Fetch;

        // L1 before any browser work. A friendly refusal, not the boundary: the egress proxy is.
        await guard.CheckAsync(fetch.Url, ct);

        using var origin = origins.Enter(fetch.Origin);
        using var originPermit = await capacity.AcquireOriginAsync(origin.State, Remaining(run), ct);

        // HTTP first: a page that needs no browser is answered without one. A request
        // that waits for a selector asks for rendered content, so it goes straight to the browser.
        if (_options.HttpFirst.Enabled && fetch.ReadySelector is null && await TryHttpFirstAsync(run, origin, ct) is { } answered)
        {
            return answered;
        }

        using var browserPermit = await capacity.AcquireBrowserAsync(Remaining(run), ct);
        var (page, document) = await NavigateAsync(run, origin, ct);

        var extractStarted = time.GetTimestamp();
        var baseUrl = ContentExtractor.BaseUrl(document, page.FinalUrl);
        var extracted = await extractor.ExtractAsync(document, fetch, ct);
        var extractMs = (long)time.GetElapsedTime(extractStarted).TotalMilliseconds;
        if (extracted is null)
        {
            // The site answered; only its content was unusable.
            origins.ReportSuccess(origin);
            await DumpAsync(page, ct);
            throw new FetchException(FetchErrorKind.ContentNotFound, "No usable main content was found on the page.") { TargetStatus = page.Status, Attempts = run.Attempts };
        }

        var convertStarted = time.GetTimestamp();
        ConvertedMarkdown markdown;
        try
        {
            markdown = converter.Convert(extracted.Content, baseUrl, fetch.IncludeLinks, fetch.IncludeImages, fetch.MaxChars, extracted.Strategy == ContentExtractor.Listing);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DumpAsync(page, ct);
            throw new FetchException(FetchErrorKind.ConversionFailed, "The page could not be converted to Markdown.", ex) { TargetStatus = page.Status, Attempts = run.Attempts };
        }

        origins.ReportSuccess(origin);
        return Result(fetch, page, extracted, markdown, run.Attempts, extractMs, (long)time.GetElapsedTime(convertStarted).TotalMilliseconds, FetchRenderers.Browser);
    }

    /// <summary>
    /// The HTTP-first attempt: the answer when the plain response is usable HTML that is not an app shell and extracts,
    /// else null and the fetch renders in the browser, with nothing counted against the origin. Two outcomes end the fetch
    /// here: the egress proxy's refusal (the browser would be refused too) and a response that is not HTML.
    /// </summary>
    private async Task<FetchResult?> TryHttpFirstAsync(FetchRun run, OriginHandle origin, CancellationToken ct)
    {
        var fetch = run.Fetch;
        var remaining = Remaining(run);
        var page = await http.LoadAsync(fetch, remaining < _options.HttpFirst.Timeout ? remaining : _options.HttpFirst.Timeout, ct);
        if (page is null)
        {
            return null;
        }

        var document = page.IsHtml ? ContentExtractor.Parse(page.Html) : null;
        var verdict = classifier.Classify(page, document);
        if (verdict.ProxyDenied)
        {
            LogProxyDenied(fetch.Url.Host);
            throw new FetchException(verdict.Error!.Value, verdict.Message) { Attempts = 1 };
        }

        if (verdict.Error == FetchErrorKind.UnsupportedContent)
        {
            throw new FetchException(FetchErrorKind.UnsupportedContent, verdict.Message) { TargetStatus = page.Status, Attempts = 1 };
        }

        if (verdict.Error is { } error)
        {
            LogHttpFirstRefused(fetch.Url.Host, error);
            return null;
        }

        if (AppShell.IsShell(document!, _options.Navigation.MinimumRenderedTextCharacters))
        {
            LogHttpFirstDeclined(fetch.Url.Host, "app shell");
            return null;
        }

        var extractStarted = time.GetTimestamp();
        var baseUrl = ContentExtractor.BaseUrl(document!, page.FinalUrl);
        var extracted = await extractor.ExtractAsync(document!, fetch, ct);
        var extractMs = (long)time.GetElapsedTime(extractStarted).TotalMilliseconds;
        if (extracted is null || AppShell.AsksForScript(extracted.Content, _options.Extraction.MinimumContentCharacters))
        {
            LogHttpFirstDeclined(fetch.Url.Host, extracted is null ? "no content" : "asks for JavaScript");
            return null;
        }

        var convertStarted = time.GetTimestamp();
        ConvertedMarkdown markdown;
        try
        {
            markdown = converter.Convert(extracted.Content, baseUrl, fetch.IncludeLinks, fetch.IncludeImages, fetch.MaxChars, extracted.Strategy == ContentExtractor.Listing);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogHttpFirstConversionFailed(fetch.Url.Host, ex);
            return null;
        }

        origins.ReportSuccess(origin);
        run.Attempts = 1;
        return Result(fetch, page, extracted, markdown, 1, extractMs, (long)time.GetElapsedTime(convertStarted).TotalMilliseconds, FetchRenderers.Http);
    }

    private static FetchResult Result(
        NormalizedFetch fetch, RenderedPage page, ExtractedContent extracted, ConvertedMarkdown markdown, int attempts, long extractMs, long convertMs, string renderer) =>
        new(
            fetch.Url,
            page.FinalUrl,
            extracted.Title,
            markdown.Markdown,
            markdown.Truncated,
            extracted.Metadata,
            new FetchDiagnostics(
                page.Status,
                extracted.Strategy,
                extracted.Quality,
                attempts,
                page.ReadinessTimedOut,
                page.HtmlBytes,
                markdown.Markdown.Length,
                page.RenderMs,
                extractMs,
                convertMs,
                Cached: false,
                page.NavigationMs,
                page.ReadinessMs,
                renderer));

    /// <summary>The attempt loop. Returns a page that passed classification, parsed once.</summary>
    private async Task<(RenderedPage Page, IHtmlDocument Document)> NavigateAsync(FetchRun run, OriginHandle origin, CancellationToken ct)
    {
        var fetch = run.Fetch;
        var maxAttempts = _options.Budget.NavigationAttempts;
        while (true)
        {
            run.Attempts++;
            var attempt = await AttemptAsync(run, ct);
            var last = run.Attempts >= maxAttempts;

            if (attempt.Failure is { } failure)
            {
                if (failure.ProxyDenied)
                {
                    LogProxyDenied(fetch.Url.Host);
                }

                if (failure.BrowserFailure)
                {
                    // Another attempt runs on the browser that replaces the one that failed.
                    if (!last)
                    {
                        continue;
                    }

                    throw new FetchException(FetchErrorKind.BrowserUnavailable, failure.Message, failure.Cause) { RetryAfter = TimeSpan.FromSeconds(5), Attempts = run.Attempts };
                }

                if (failure.Transient)
                {
                    origins.ReportTransientFailure(origin);
                    if (await TryBackOffAsync(run, null, last, ct))
                    {
                        continue;
                    }
                }

                throw new FetchException(failure.Kind, failure.Message, failure.Cause) { Attempts = run.Attempts };
            }

            var page = attempt.Page!;
            var document = page.IsHtml ? ContentExtractor.Parse(page.Html) : null;
            var verdict = classifier.Classify(page, document);
            if (verdict.Error is null)
            {
                // Classification passes only HTML, and HTML was parsed above.
                return (page, document!);
            }

            if (verdict.ProxyDenied)
            {
                LogProxyDenied(fetch.Url.Host);
            }

            if (verdict.Block.IsBlocked)
            {
                // Never retried, never solved: the origin's block circuit stops other fetches provoking more challenges.
                origins.ReportBlock(origin, verdict.Block.Kind, verdict.Block.Provider);
                throw new FetchException(verdict.Error.Value, verdict.Message) { TargetStatus = page.Status, Provider = verdict.Block.Provider, Attempts = run.Attempts };
            }

            if (verdict.Transient)
            {
                origins.ReportTransientFailure(origin);
                if (await TryBackOffAsync(run, verdict.RetryAfter, last, ct))
                {
                    continue;
                }

                throw new FetchException(verdict.Error.Value, verdict.Message) { TargetStatus = page.Status, RetryAfter = verdict.RetryAfter, Attempts = run.Attempts };
            }

            // A definite answer (404, 403, not HTML) means the site is up.
            origins.ReportSuccess(origin);
            throw new FetchException(verdict.Error.Value, verdict.Message) { TargetStatus = page.Status, Attempts = run.Attempts };
        }
    }

    /// <summary>One navigation on a fresh context. The context is closed before the page is parsed, to free the browser sooner.</summary>
    private async Task<Attempt> AttemptAsync(FetchRun run, CancellationToken ct)
    {
        var lease = await browsers.AcquireAsync(ct);
        FetchContext context;
        try
        {
            context = await contexts.CreateAsync(lease);
        }
        catch (PlaywrightException) when (!lease.Generation.IsAlive)
        {
            lease.Dispose();
            return new Attempt(null, new NavigationFailure(FetchErrorKind.BrowserUnavailable, true, true, "The browser failed during the fetch."));
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        await using (context)
        {
            try
            {
                using var _ = new DenialReport(context, run.Fetch.Url.Host, this);
                var page = await renderer.RenderAsync(context, run.Fetch, Remaining(run), ct);
                return context.DownloadAttempted
                    ? new Attempt(null, new NavigationFailure(FetchErrorKind.UnsupportedContent, false, false, "The target is a file download, not an HTML page."))
                    : new Attempt(page, null);
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                return new Attempt(null, await NavigationFailures.ClassifyAsync(ex, context, time));
            }
        }
    }

    /// <summary>
    /// Waits before the next attempt: the target's Retry-After when it sent one, else exponential backoff with jitter.
    /// False when this was the last attempt or the wait would not fit the remaining budget.
    /// </summary>
    private async Task<bool> TryBackOffAsync(FetchRun run, TimeSpan? retryAfter, bool last, CancellationToken ct)
    {
        if (last)
        {
            return false;
        }

        var backoff = _options.Budget.RetryBackoff * Math.Pow(2, run.Attempts - 1) * (0.5 + Random.Shared.NextDouble());
        var delay = retryAfter ?? backoff;
        if (delay >= Remaining(run))
        {
            return false;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, time, ct);
        }

        return true;
    }

    private TimeSpan Remaining(FetchRun run) => run.Fetch.Budget - time.GetElapsedTime(run.Started);

    /// <summary>Opt-in diagnostics: the rendered HTML of a fetch whose content could not be used. Page content, so sensitive.</summary>
    private async Task DumpAsync(RenderedPage page, CancellationToken ct)
    {
        var diagnostics = _options.Diagnostics;
        if (!diagnostics.DumpOnFailure || diagnostics.DumpDirectory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(diagnostics.DumpDirectory);
            var path = Path.Combine(diagnostics.DumpDirectory, $"{time.GetUtcNow():yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.html");
            await File.WriteAllTextAsync(path, page.Html, ct);
            LogDumped(page.FinalUrl.Host, path);
        }
        catch (IOException ex)
        {
            // A diagnostic that cannot be written must not change the fetch's outcome.
            LogDumpFailed(ex);
        }
    }

    private sealed class FetchRun(NormalizedFetch fetch, long started)
    {
        public NormalizedFetch Fetch { get; } = fetch;
        public long Started { get; } = started;
        public int Attempts { get; set; }
    }

    private sealed record Attempt(RenderedPage? Page, NavigationFailure? Failure);

    /// <summary>Logs what the route guard refused during one attempt, however the attempt ends.</summary>
    private readonly struct DenialReport(FetchContext context, string host, WebFetchService service) : IDisposable
    {
        public void Dispose()
        {
            if (context.Denials.First is { } first)
            {
                service.LogRouteDenied(host, context.Denials.Count, first.Reason);
            }
        }
    }

    // Host only: never the full URL or its query string.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Fetched {Host} with {Strategy} after {Attempts} attempt(s): render {RenderMs} ms, extract {ExtractMs} ms, convert {ConvertMs} ms, {Chars} chars")]
    private partial void LogFetched(string host, string strategy, int attempts, long renderMs, long extractMs, long convertMs, int chars);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fetch of {Host} failed: {Kind} after {Attempts} attempt(s)")]
    private partial void LogFailed(string host, FetchErrorKind kind, int attempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fetch of {Host} refused before any browser work: {Reason}")]
    private partial void LogNotAllowed(string host, string reason);

    // A request that reaches the proxy and is refused means L1 and L2 missed something (a rebinding attempt, a redirect
    // hop, a host L2 had not seen): a security signal.
    [LoggerMessage(Level = LogLevel.Warning, Message = "The egress proxy refused a request while fetching {Host}")]
    private partial void LogProxyDenied(string host);

    [LoggerMessage(Level = LogLevel.Information, Message = "The route guard refused {Count} request(s) while fetching {Host}; first: {Reason}")]
    private partial void LogRouteDenied(string host, int count, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HTTP-first answer for {Host} not used ({Reason}); rendering in the browser")]
    private partial void LogHttpFirstDeclined(string host, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HTTP-first answer for {Host} not used ({Kind}); rendering in the browser")]
    private partial void LogHttpFirstRefused(string host, FetchErrorKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HTTP-first answer for {Host} could not be converted; rendering in the browser")]
    private partial void LogHttpFirstConversionFailed(string host, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wrote the rendered page of {Host} to {Path}")]
    private partial void LogDumped(string host, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write a diagnostic page dump")]
    private partial void LogDumpFailed(Exception exception);
}
