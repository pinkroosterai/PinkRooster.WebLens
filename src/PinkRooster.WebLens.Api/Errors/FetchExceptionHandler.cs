using System.Globalization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PinkRooster.WebLens.Api.Endpoints;
using PinkRooster.WebLens.Fetch;

namespace PinkRooster.WebLens.Api.Errors;

/// <summary>
/// The one place fetch failures become HTTP: target refusals are 422, target failures 502, our own
/// capacity or browser 503, deadlines 504. It logs for itself, because from .NET 10 the exception middleware does not
/// log an exception a handler reports as handled.
/// </summary>
internal sealed partial class FetchExceptionHandler(IProblemDetailsService problems, ILogger<FetchExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not FetchException ex)
        {
            return false;
        }

        var (status, type, title) = Classify(ex.Kind);

        // Retry-After only where retrying can help: the target's own hint (502) or our capacity (503).
        var retryAfterSeconds = RetryAfterSeconds(ex, status);
        if (retryAfterSeconds is { } seconds)
        {
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        httpContext.Response.StatusCode = status;
        Log(logger, status, ex);

        // Fetch messages never carry the target URL, credentials or page content (FetchException), so the detail is safe to return.
        // A conversion failure is our bug: its detail stays out of the response like any other 500.
        var problem = ex.Kind == FetchErrorKind.InvalidRequest
            ? new HttpValidationProblemDetails(new Dictionary<string, string[]> { [FetchMapping.ToContractField(ex.Field)] = [ex.Message] })
            : new ProblemDetails();
        problem.Status = status;
        problem.Type = type;
        problem.Title = title;
        problem.Detail = status == StatusCodes.Status500InternalServerError ? null : ex.Message;
        problem.Extensions["errorKind"] = ex.Kind.ToString();
        problem.Extensions["targetStatus"] = ex.TargetStatus;
        problem.Extensions["retryAfterSeconds"] = retryAfterSeconds;

        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
    }

    /// <summary>The error mapping for fetch: the status, problem type and title of each failure. Shared with the MCP tools.</summary>
    internal static (int Status, string Type, string Title) Classify(FetchErrorKind kind) => kind switch
    {
        FetchErrorKind.InvalidRequest => (StatusCodes.Status400BadRequest, ProblemTypes.Validation, "The request is not valid."),
        FetchErrorKind.TargetNotAllowed => (StatusCodes.Status422UnprocessableEntity, ProblemTypes.TargetNotAllowed, "The target URL is not allowed."),
        FetchErrorKind.TargetNotFound => (StatusCodes.Status422UnprocessableEntity, ProblemTypes.TargetNotFound, "The target page does not exist."),
        FetchErrorKind.TargetAccessDenied => (StatusCodes.Status422UnprocessableEntity, ProblemTypes.TargetAccessDenied, "The target site refused access."),
        FetchErrorKind.BotChallenge => (StatusCodes.Status422UnprocessableEntity, ProblemTypes.BotChallenge, "The target site served a bot challenge."),
        FetchErrorKind.CaptchaRequired => (StatusCodes.Status422UnprocessableEntity, ProblemTypes.CaptchaRequired, "The target site asks for a CAPTCHA."),
        FetchErrorKind.UnsupportedContent => (StatusCodes.Status422UnprocessableEntity, ProblemTypes.UnsupportedContent, "The target is not an HTML page."),
        FetchErrorKind.ContentNotFound => (StatusCodes.Status422UnprocessableEntity, ProblemTypes.ContentNotFound, "No usable main content was found."),
        FetchErrorKind.TargetUnavailable => (StatusCodes.Status502BadGateway, ProblemTypes.TargetUnavailable, "The target site is unavailable."),
        FetchErrorKind.CapacityExceeded => (StatusCodes.Status503ServiceUnavailable, ProblemTypes.CapacityExceeded, "The service is at capacity."),
        FetchErrorKind.BrowserUnavailable => (StatusCodes.Status503ServiceUnavailable, ProblemTypes.BrowserUnavailable, "The browser is not available."),
        FetchErrorKind.Timeout => (StatusCodes.Status504GatewayTimeout, ProblemTypes.Timeout, "The fetch timed out."),
        _ => (StatusCodes.Status500InternalServerError, ProblemTypes.Internal, "An unexpected error occurred."),
    };

    internal static int? RetryAfterSeconds(FetchException ex, int status) =>
        ex.RetryAfter is { } ra && status is StatusCodes.Status502BadGateway or StatusCodes.Status503ServiceUnavailable
            ? (int)Math.Ceiling(Math.Max(ra.TotalSeconds, 1))
            : null;

    internal static void Log(ILogger logger, int status, FetchException ex)
    {
        // Expected outcomes (bad request, target refusals) are Information; target and capacity trouble Warning; our bugs Error.
        if (status < 500)
        {
            LogExpected(logger, ex.Kind, status, ex.TargetStatus, ex.Provider);
        }
        else if (status is StatusCodes.Status502BadGateway or StatusCodes.Status503ServiceUnavailable or StatusCodes.Status504GatewayTimeout)
        {
            LogUpstream(logger, ex.Kind, status, ex.TargetStatus, ex.Attempts);
        }
        else
        {
            LogUnexpected(logger, ex.Kind, status, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Fetch refused: {Kind} ({Status}, target status {TargetStatus}, provider {Provider})")]
    private static partial void LogExpected(ILogger logger, FetchErrorKind kind, int status, int? targetStatus, string? provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fetch failed: {Kind} ({Status}, target status {TargetStatus}) after {Attempts} attempt(s)")]
    private static partial void LogUpstream(ILogger logger, FetchErrorKind kind, int status, int? targetStatus, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Fetch failed unexpectedly: {Kind} ({Status})")]
    private static partial void LogUnexpected(ILogger logger, FetchErrorKind kind, int status, Exception exception);
}
