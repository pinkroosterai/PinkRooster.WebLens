using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace PinkRooster.WebLens.Fetch;

/// <summary>One fetch's browser context and its single page. Disposing closes both and returns the browser lease.</summary>
internal sealed partial class FetchContext(BrowserLease lease, IBrowserContext context, IPage page, RouteDenials denials, DataRequests dataRequests, ILogger logger) : IAsyncDisposable
{
    private int _downloadAttempted;

    /// <summary>Requests the route guard (L2) refused in this context.</summary>
    public RouteDenials Denials { get; } = denials;

    /// <summary>The page's fetch, XHR and script requests in flight, for readiness.</summary>
    public DataRequests DataRequests { get; } = dataRequests;

    /// <summary>The context sends its traffic through the egress proxy.</summary>
    public bool ThroughProxy { get; init; }

    public BrowserLease Lease { get; } = lease;
    public IBrowserContext Context { get; } = context;
    public IPage Page { get; } = page;

    /// <summary>The page tried to download a file; downloads are refused, and the fetch reports unsupported content.</summary>
    public bool DownloadAttempted => Volatile.Read(ref _downloadAttempted) == 1;

    internal void MarkDownload() => Volatile.Write(ref _downloadAttempted, 1);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Context.CloseAsync();
        }
        catch (PlaywrightException ex)
        {
            // Usually the browser died underneath us; the original error, if any, is what the caller sees.
            LogCloseFailed(Lease.Generation.Number, ex);
        }
        finally
        {
            Lease.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Closing a browser context on generation {Generation} failed")]
    private partial void LogCloseFailed(int generation, Exception exception);
}

/// <summary>What the route guard refused in one context: counted, with the first reason kept for the log.</summary>
internal sealed class RouteDenials
{
    private int _count;
    private GuardDenial? _first;

    public int Count => Volatile.Read(ref _count);
    public GuardDenial? First => Volatile.Read(ref _first);

    public void Add(GuardDenial denial)
    {
        Interlocked.CompareExchange(ref _first, denial, null);
        Interlocked.Increment(ref _count);
    }
}

/// <summary>
/// Creates the hardened, operator-owned context every fetch runs in, bound to the egress proxy (L3) and guarded per
/// request (L2). L2 does not see redirect hops, since Playwright's route handler is not called for them: those are the proxy's, and <see cref="PageRenderer"/> checks the chain afterwards.
/// </summary>
internal sealed partial class ContextFactory(IOptions<FetchOptions> options, UrlGuard guard, ILogger<FetchContext> logger)
{
    private readonly FetchOptions _options = options.Value;

    public async Task<FetchContext> CreateAsync(BrowserLease lease)
    {
        var browser = _options.Browser;
        var contextOptions = new BrowserNewContextOptions
        {
            Locale = browser.Locale,
            TimezoneId = browser.TimezoneId,
            ViewportSize = new ViewportSize { Width = browser.ViewportWidth, Height = browser.ViewportHeight },
            DeviceScaleFactor = browser.DeviceScaleFactor,
            UserAgent = browser.UserAgent,

            // Locale and the language header agree, so the profile is coherent.
            ExtraHTTPHeaders = new Dictionary<string, string> { ["Accept-Language"] = browser.Locale },
            ServiceWorkers = ServiceWorkerPolicy.Block,
            AcceptDownloads = false,
            IgnoreHTTPSErrors = false,
            Permissions = [],
            Proxy = Proxy(_options.Security.EgressProxy),
        };

        var context = await lease.Browser.NewContextAsync(contextOptions);
        var denials = new RouteDenials();
        try
        {
            var policy = _options.Resources.Policy;
            await context.RouteAsync("**/*", route =>
            {
                if (Denied(route.Request.Url) is { } denial)
                {
                    denials.Add(denial);
                    return route.AbortAsync("blockedbyclient");
                }

                return Blocked(policy, route.Request.ResourceType) ? route.AbortAsync() : route.FallbackAsync();
            });
            await context.RouteWebSocketAsync("**/*", socket =>
            {
                if (Denied(socket.Url) is { } denial)
                {
                    denials.Add(denial);
                    _ = CloseQuietly(socket);
                    return;
                }

                socket.ConnectToServer();
            });

            var page = await context.NewPageAsync();
            var dataRequests = new DataRequests();
            dataRequests.Attach(page);
            var fetchContext = new FetchContext(lease, context, page, denials, dataRequests, logger) { ThroughProxy = contextOptions.Proxy is not null };

            // One page per fetch: anything the page opens is closed at once. A page must not be able to hang a fetch.
            context.Page += (_, opened) => _ = CloseQuietly(opened);
            page.Dialog += (_, dialog) => _ = DismissQuietly(dialog);
            page.Download += (_, download) =>
            {
                fetchContext.MarkDownload();
                _ = CancelQuietly(download);
            };

            return fetchContext;
        }
        catch
        {
            await context.CloseAsync();
            throw;
        }
    }

    private static bool Blocked(ResourcePolicy policy, string resourceType) => policy switch
    {
        ResourcePolicy.NoMedia => resourceType == "media",
        ResourcePolicy.Minimal => resourceType is "media" or "image" or "font",
        _ => false,
    };

    /// <summary>The synchronous half of the URL guard, for every request the page makes. Non-network schemes are not ours to judge.</summary>
    private GuardDenial? Denied(string raw) =>
        Uri.TryCreate(raw, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" or "ws" or "wss" ? guard.CheckWithoutDns(url) : null;

    /// <summary>
    /// Every context goes through the egress proxy when one is configured. <c>&lt;-loopback&gt;</c> removes Chromium's
    /// implicit bypass for loopback, so even <c>localhost</c> has to pass the proxy's deny list.
    /// </summary>
    private static Proxy? Proxy(EgressProxyOptions proxy) => string.IsNullOrWhiteSpace(proxy.Server)
        ? null
        : new Proxy { Server = proxy.Server, Username = proxy.Username, Password = proxy.Password, Bypass = "<-loopback>" };

    // These run on Playwright's event thread with nothing awaiting them. A page that is already gone is the usual cause
    // of a failure, so it is logged at Debug and the fetch carries on.
    private async Task CloseQuietly(IPage page)
    {
        try
        {
            await page.CloseAsync();
        }
        catch (PlaywrightException ex)
        {
            LogHandlerFailed("close a popup", ex);
        }
    }

    private async Task CloseQuietly(IWebSocketRoute socket)
    {
        try
        {
            await socket.CloseAsync();
        }
        catch (PlaywrightException ex)
        {
            LogHandlerFailed("close a refused WebSocket", ex);
        }
    }

    private async Task DismissQuietly(IDialog dialog)
    {
        try
        {
            await dialog.DismissAsync();
        }
        catch (PlaywrightException ex)
        {
            LogHandlerFailed("dismiss a dialog", ex);
        }
    }

    private async Task CancelQuietly(IDownload download)
    {
        try
        {
            await download.CancelAsync();
        }
        catch (PlaywrightException ex)
        {
            LogHandlerFailed("cancel a download", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not {Action}")]
    private partial void LogHandlerFailed(string action, Exception exception);
}
