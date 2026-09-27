using System.Globalization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PinkRooster.WebLens.Api.Endpoints;
using PinkRooster.WebLens.Search;

namespace PinkRooster.WebLens.Api.Errors;

/// <summary>
/// The one place search failures become HTTP. It logs for itself, because from .NET 10 the exception
/// middleware does not log an exception a handler reports as handled.
/// </summary>
internal sealed partial class SearchExceptionHandler(IProblemDetailsService problems, ILogger<SearchExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not SearchException ex)
        {
            return false;
        }

        var (status, type, title) = Classify(ex.Kind);
        var retryAfterSeconds = RetryAfterSeconds(ex, status);
        if (retryAfterSeconds is { } seconds)
        {
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        httpContext.Response.StatusCode = status;
        Log(logger, status, ex);

        // The module's messages never contain query text, instance URLs or credentials, so the detail is safe to return.
        // An invalid query carries the same errors map as the host's own validation, so a client reads every 400 one way.
        var problem = ex.Kind == SearchErrorKind.InvalidQuery
            ? new HttpValidationProblemDetails(new Dictionary<string, string[]> { [SearchMapping.ToContractField(ex.Field)] = [ex.Message] })
            : new ProblemDetails();
        problem.Status = status;
        problem.Type = type;
        problem.Title = title;
        problem.Detail = ex.Message;
        problem.Extensions["errorKind"] = ex.Kind.ToString();
        problem.Extensions["retryAfterSeconds"] = retryAfterSeconds;

        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
    }

    /// <summary>The error mapping for search: the status, problem type and title of each failure. Shared with the MCP tools.</summary>
    internal static (int Status, string Type, string Title) Classify(SearchErrorKind kind) => kind switch
    {
        SearchErrorKind.InvalidQuery => (StatusCodes.Status400BadRequest, ProblemTypes.Validation, "The request is not valid."),
        SearchErrorKind.QueryUnsupported => (StatusCodes.Status400BadRequest, ProblemTypes.SearchQueryUnsupported, "The search query uses syntax this service does not support."),
        SearchErrorKind.NoEligibleInstance => (StatusCodes.Status503ServiceUnavailable, ProblemTypes.SearchUnavailable, "No search instance is available right now."),
        SearchErrorKind.AllAttemptsFailed => (StatusCodes.Status502BadGateway, ProblemTypes.SearchFailed, "The search could not be completed."),
        SearchErrorKind.Timeout => (StatusCodes.Status504GatewayTimeout, ProblemTypes.Timeout, "The search timed out."),
        _ => (StatusCodes.Status500InternalServerError, ProblemTypes.Internal, "An unexpected error occurred."),
    };

    /// <summary>Only for 503: when an instance comes out of its cooldown.</summary>
    internal static int? RetryAfterSeconds(SearchException ex, int status) =>
        ex.RetryAfter is { } ra && status == StatusCodes.Status503ServiceUnavailable ? (int)Math.Ceiling(Math.Max(ra.TotalSeconds, 1)) : null;

    internal static void Log(ILogger logger, int status, SearchException ex)
    {
        // Expected outcomes are Information; upstream trouble is Warning; anything else Error.
        if (status < 500)
        {
            LogExpected(logger, ex.Kind, status);
        }
        else if (status is StatusCodes.Status502BadGateway or StatusCodes.Status503ServiceUnavailable or StatusCodes.Status504GatewayTimeout)
        {
            LogUpstream(logger, ex.Kind, status, ex.Attempts.Count);
        }
        else
        {
            LogUnexpected(logger, ex.Kind, status);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Search rejected: {Kind} ({Status})")]
    private static partial void LogExpected(ILogger logger, SearchErrorKind kind, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Search failed: {Kind} ({Status}) after {Attempts} attempt(s)")]
    private static partial void LogUpstream(ILogger logger, SearchErrorKind kind, int status, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Search failed unexpectedly: {Kind} ({Status})")]
    private static partial void LogUnexpected(ILogger logger, SearchErrorKind kind, int status);
}
