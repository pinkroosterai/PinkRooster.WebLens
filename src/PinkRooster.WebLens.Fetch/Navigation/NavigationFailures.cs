using Microsoft.Playwright;

namespace PinkRooster.WebLens.Fetch;

/// <summary>How a navigation that threw is treated: which error, and whether another attempt may help.</summary>
internal sealed record NavigationFailure(FetchErrorKind Kind, bool Transient, bool BrowserFailure, string Message)
{
    /// <summary>The egress proxy refused the connection: a security signal, logged at Warning.</summary>
    public bool ProxyDenied { get; init; }

    /// <summary>What Playwright threw. Kept for logs only: its message can contain the target URL.</summary>
    public Exception? Cause { get; init; }
}

/// <summary>Turns Playwright's exceptions into fetch outcomes. Chromium reports network failures as <c>net::ERR_*</c> codes in the message.</summary>
internal static class NavigationFailures
{
    private static readonly string[] TransientNetworkCodes =
    [
        "ERR_NAME_NOT_RESOLVED", "ERR_NAME_RESOLUTION_FAILED", "ERR_CONNECTION_REFUSED", "ERR_CONNECTION_RESET", "ERR_CONNECTION_CLOSED",
        "ERR_CONNECTION_TIMED_OUT", "ERR_CONNECTION_FAILED", "ERR_TIMED_OUT", "ERR_ADDRESS_UNREACHABLE", "ERR_NETWORK_CHANGED",
        "ERR_EMPTY_RESPONSE", "ERR_INTERNET_DISCONNECTED", "ERR_TUNNEL_CONNECTION_FAILED", "ERR_PROXY_CONNECTION_FAILED",
        "ERR_HTTP2_PROTOCOL_ERROR", "ERR_NETWORK_IO_SUSPENDED",
    ];

    /// <summary>How long a navigation aborted by a crash may take to show up as a disconnected browser.</summary>
    private static readonly TimeSpan DisconnectGrace = TimeSpan.FromMilliseconds(500);

    public static async Task<NavigationFailure> ClassifyAsync(Exception exception, FetchContext context, TimeProvider time)
    {
        // A crash aborts the navigation before the browser reports the disconnect. ERR_ABORTED has innocent causes too
        // (a 204, a cancelled download), so the browser gets a moment to say whether it is still there.
        if (exception.Message.Contains("net::ERR_ABORTED", StringComparison.Ordinal) && !context.DownloadAttempted)
        {
            var started = time.GetTimestamp();
            while (Connected(context) && time.GetElapsedTime(started) < DisconnectGrace)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), time);
            }
        }

        return Describe(exception, context) with { Cause = exception };
    }

    private static bool Connected(FetchContext context) => context.Lease.Generation.IsAlive && context.Lease.Browser.IsConnected;

    private static NavigationFailure Describe(Exception exception, FetchContext context)
    {
        // A browser that went away mid-fetch is ours to recover, not the target's fault.
        if (!Connected(context))
        {
            return new(FetchErrorKind.BrowserUnavailable, true, true, "The browser failed during the fetch.");
        }

        if (exception is TimeoutException)
        {
            return new(FetchErrorKind.Timeout, false, false, "The target page did not load in time.");
        }

        var message = exception.Message;
        if (context.DownloadAttempted || message.Contains("Download is starting", StringComparison.Ordinal))
        {
            return new(FetchErrorKind.UnsupportedContent, false, false, "The target is a file download, not an HTML page.");
        }

        // Our own route guard (L2) aborted the main document.
        if (message.Contains("net::ERR_BLOCKED_BY_CLIENT", StringComparison.Ordinal))
        {
            return new(FetchErrorKind.TargetNotAllowed, false, false, "The target URL is not allowed.");
        }

        // Smokescreen answers a refused CONNECT with 407, which Chromium reports this way.
        // Callers hear "unavailable", as for any unreachable target; the log hears "denied".
        if (message.Contains("net::ERR_PROXY_AUTH_UNSUPPORTED", StringComparison.Ordinal))
        {
            return new(FetchErrorKind.TargetUnavailable, false, false, "The target site could not be reached.") { ProxyDenied = true };
        }

        // A redirect the proxy refused ends on Chromium's error page.
        if (message.Contains("interrupted by another navigation to \"chrome-error://", StringComparison.Ordinal))
        {
            return new(FetchErrorKind.TargetUnavailable, false, false, "The target site could not be reached.") { ProxyDenied = context.ThroughProxy };
        }

        if (TransientNetworkCodes.Any(code => message.Contains(code, StringComparison.Ordinal)))
        {
            return new(FetchErrorKind.TargetUnavailable, true, false, "The target site could not be reached.");
        }

        if (message.Contains("ERR_CERT_", StringComparison.Ordinal) || message.Contains("ERR_SSL_", StringComparison.Ordinal))
        {
            return new(FetchErrorKind.TargetUnavailable, false, false, "The target site's TLS certificate is not valid.");
        }

        if (exception is PlaywrightException && message.Contains("closed", StringComparison.OrdinalIgnoreCase))
        {
            return new(FetchErrorKind.BrowserUnavailable, true, true, "The browser failed during the fetch.");
        }

        return new(FetchErrorKind.TargetUnavailable, false, false, "The target page could not be loaded.");
    }
}
