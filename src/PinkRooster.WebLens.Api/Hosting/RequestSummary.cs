using System.Diagnostics;
using System.Security.Claims;

namespace PinkRooster.WebLens.Api.Hosting;

/// <summary>
/// One summary line per request: route, key id, status, duration, and for the two capabilities whether
/// the cache answered and how many attempts it took. Outermost, so it sees the status the exception handlers wrote.
/// Never the body, the query string, the search text or the target URL: both endpoints and the MCP tools carry those in the body.
/// </summary>
internal static partial class RequestSummary
{
    public const string CachedItem = "weblens.cached";
    public const string AttemptsItem = "weblens.attempts";

    public static WebApplication UseWebLensRequestSummary(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PinkRooster.WebLens.Api.Requests");
        app.Use(async (http, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await next(http);
            }
            finally
            {
                var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)";
                var keyId = http.User.FindFirstValue(Scopes.KeyIdClaimType) ?? "-";
                var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var cached = http.Items.TryGetValue(CachedItem, out var c) ? c : null;
                var attempts = http.Items.TryGetValue(AttemptsItem, out var a) ? a : null;
                var status = http.RequestAborted.IsCancellationRequested && !http.Response.HasStarted ? 499 : http.Response.StatusCode;

                // The capabilities (HTTP and MCP) at Information; health probes and the rest at Debug, so probes do not drown the log.
                var level = route.StartsWith("/v1", StringComparison.Ordinal) || route.StartsWith(McpEndpoint.Path, StringComparison.Ordinal)
                    ? LogLevel.Information
                    : LogLevel.Debug;
                LogRequest(logger, level, http.Request.Method, route, status, elapsed, keyId, cached, attempts);
            }
        });
        return app;
    }

    [LoggerMessage(Message = "{Method} {Route} -> {Status} in {ElapsedMs} ms (key {KeyId}, cached {Cached}, attempts {Attempts})")]
    private static partial void LogRequest(ILogger logger, LogLevel level, string method, string route, int status, long elapsedMs, string keyId, object? cached, object? attempts);
}
