using System.Net;
using PinkRooster.WebLens.Client.Models;

namespace PinkRooster.WebLens.Client.Exceptions;

/// <summary>
/// Base exception for failures returned by the WebLens API.
/// </summary>
public class WebLensApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public WebLensProblemDetails? ProblemDetails { get; }

    public string? ProblemType => ProblemDetails?.Type;

    public string? RawResponseBody { get; }

    public WebLensApiException(
        HttpStatusCode statusCode,
        WebLensProblemDetails? problemDetails,
        string? message = null,
        Exception? innerException = null,
        string? rawResponseBody = null)
        : base(message ?? FormatMessage(statusCode, problemDetails, rawResponseBody), innerException)
    {
        StatusCode = statusCode;
        ProblemDetails = problemDetails;
        RawResponseBody = rawResponseBody;
    }

    private static string FormatMessage(HttpStatusCode statusCode, WebLensProblemDetails? problem, string? raw)
    {
        if (problem is not null)
        {
            if (!string.IsNullOrWhiteSpace(problem.Detail))
            {
                return problem.Detail;
            }

            if (!string.IsNullOrWhiteSpace(problem.Title))
            {
                return problem.Title;
            }
        }

        if (!string.IsNullOrWhiteSpace(raw))
        {
            var snippet = raw.Length > 200 ? string.Concat(raw.AsSpan(0, 200), "...") : raw;
            return $"WebLens API returned {(int)statusCode} {statusCode}: {snippet}";
        }

        return $"WebLens API returned {(int)statusCode} {statusCode}.";
    }
}

/// <summary>
/// Thrown when the client is rate-limited (HTTP 429).
/// </summary>
public sealed class WebLensRateLimitedException : WebLensApiException
{
    public TimeSpan? RetryAfter { get; }

    public WebLensRateLimitedException(
        HttpStatusCode statusCode,
        WebLensProblemDetails? problemDetails,
        TimeSpan? retryAfter,
        string? rawResponseBody = null)
        : base(statusCode, problemDetails, null, null, rawResponseBody)
    {
        RetryAfter = retryAfter;
    }
}

/// <summary>
/// Thrown when request validation fails (HTTP 400).
/// </summary>
public sealed class WebLensValidationException : WebLensApiException
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public WebLensValidationException(
        HttpStatusCode statusCode,
        WebLensProblemDetails? problemDetails,
        IReadOnlyDictionary<string, string[]>? errors,
        string? rawResponseBody = null)
        : base(statusCode, problemDetails, null, null, rawResponseBody)
    {
        Errors = errors ?? new Dictionary<string, string[]>();
    }
}

/// <summary>
/// Thrown when a target site serves a bot challenge or CAPTCHA requirement (HTTP 422).
/// </summary>
public sealed class WebLensChallengeException : WebLensApiException
{
    public WebLensChallengeException(
        HttpStatusCode statusCode,
        WebLensProblemDetails? problemDetails,
        string? rawResponseBody = null)
        : base(statusCode, problemDetails, null, null, rawResponseBody)
    {
    }
}

/// <summary>
/// Thrown when a target URL is not allowed (HTTP 422).
/// </summary>
public sealed class WebLensTargetNotAllowedException : WebLensApiException
{
    public WebLensTargetNotAllowedException(
        HttpStatusCode statusCode,
        WebLensProblemDetails? problemDetails,
        string? rawResponseBody = null)
        : base(statusCode, problemDetails, null, null, rawResponseBody)
    {
    }
}

/// <summary>
/// Thrown when a target domain or host cannot be found or does not exist (HTTP 422).
/// </summary>
public sealed class WebLensTargetNotFoundException : WebLensApiException
{
    public WebLensTargetNotFoundException(
        HttpStatusCode statusCode,
        WebLensProblemDetails? problemDetails,
        string? rawResponseBody = null)
        : base(statusCode, problemDetails, null, null, rawResponseBody)
    {
    }
}

/// <summary>
/// Thrown when the WebLens service or upstream dependencies are unavailable or timed out (HTTP 502/503/504).
/// </summary>
public sealed class WebLensUnavailableException : WebLensApiException
{
    public TimeSpan? RetryAfter { get; }

    public WebLensUnavailableException(
        HttpStatusCode statusCode,
        WebLensProblemDetails? problemDetails,
        TimeSpan? retryAfter,
        string? rawResponseBody = null)
        : base(statusCode, problemDetails, null, null, rawResponseBody)
    {
        RetryAfter = retryAfter;
    }
}

internal static class WebLensExceptionFactory
{
    public const string ProblemPrefix = "urn:weblens:problem:";
    public const string ProblemValidation = ProblemPrefix + "validation";
    public const string ProblemRateLimited = ProblemPrefix + "rate-limited";
    public const string ProblemSearchQueryUnsupported = ProblemPrefix + "search-query-unsupported";
    public const string ProblemSearchFailed = ProblemPrefix + "search-failed";
    public const string ProblemSearchUnavailable = ProblemPrefix + "search-unavailable";
    public const string ProblemTargetNotAllowed = ProblemPrefix + "target-not-allowed";
    public const string ProblemTargetNotFound = ProblemPrefix + "target-not-found";
    public const string ProblemBotChallenge = ProblemPrefix + "bot-challenge";
    public const string ProblemCaptchaRequired = ProblemPrefix + "captcha-required";
    public const string ProblemTargetUnavailable = ProblemPrefix + "target-unavailable";
    public const string ProblemCapacityExceeded = ProblemPrefix + "capacity-exceeded";
    public const string ProblemBrowserUnavailable = ProblemPrefix + "browser-unavailable";
    public const string ProblemTimeout = ProblemPrefix + "timeout";

    public static WebLensApiException Create(
        HttpResponseMessage response,
        WebLensProblemDetails? problem,
        string? rawContent)
    {
        TimeSpan? retryAfter = null;
        if (response.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } delta)
            {
                retryAfter = delta;
            }
            else if (ra.Date is { } date)
            {
                var diff = date - DateTimeOffset.UtcNow;
                retryAfter = diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
            }
        }

        if (retryAfter is null && problem?.RetryAfterSeconds is { } seconds)
        {
            retryAfter = TimeSpan.FromSeconds(seconds);
        }

        var type = problem?.Type;

        if (type == ProblemRateLimited || response.StatusCode == (HttpStatusCode)429)
        {
            return new WebLensRateLimitedException(response.StatusCode, problem, retryAfter, rawContent);
        }

        if (type is ProblemValidation or ProblemSearchQueryUnsupported || (response.StatusCode == HttpStatusCode.BadRequest && problem?.Errors is not null))
        {
            return new WebLensValidationException(response.StatusCode, problem, problem?.Errors, rawContent);
        }

        if (type is ProblemBotChallenge or ProblemCaptchaRequired)
        {
            return new WebLensChallengeException(response.StatusCode, problem, rawContent);
        }

        if (type == ProblemTargetNotAllowed)
        {
            return new WebLensTargetNotAllowedException(response.StatusCode, problem, rawContent);
        }

        if (type == ProblemTargetNotFound)
        {
            return new WebLensTargetNotFoundException(response.StatusCode, problem, rawContent);
        }

        if (type is ProblemSearchUnavailable or ProblemSearchFailed or ProblemTargetUnavailable or ProblemCapacityExceeded or ProblemBrowserUnavailable or ProblemTimeout
            || response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            return new WebLensUnavailableException(response.StatusCode, problem, retryAfter, rawContent);
        }

        return new WebLensApiException(response.StatusCode, problem, null, null, rawContent);
    }
}
