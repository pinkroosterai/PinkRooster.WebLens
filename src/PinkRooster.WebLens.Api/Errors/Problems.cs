using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace PinkRooster.WebLens.Api.Errors;

internal static class Problems
{
    public static string TraceId(HttpContext http) => Activity.Current?.Id ?? http.TraceIdentifier;

    /// <summary>
    /// Writes a host-level problem (401, 403, 429) in the same shape the exception handlers write, through
    /// <see cref="IProblemDetailsService"/> so <see cref="Customize"/> stamps it too.
    /// </summary>
    public static async Task WriteAsync(
        IProblemDetailsService problems, HttpContext http, int status, string type, string title, string? detail, string errorKind, int? retryAfterSeconds = null)
    {
        http.Response.StatusCode = status;
        if (retryAfterSeconds is { } seconds)
        {
            http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var problem = new ProblemDetails { Status = status, Type = type, Title = title, Detail = detail };
        problem.Extensions["errorKind"] = errorKind;
        problem.Extensions["retryAfterSeconds"] = retryAfterSeconds;
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });
    }

    /// <summary>Stamps the fields every problem carries. Called for every problem written through <c>IProblemDetailsService</c>.</summary>
    public static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        problem.Extensions["traceId"] = TraceId(context.HttpContext);

        // Framework-generated problems (bad JSON, unhandled exceptions) carry an RFC 9110 URL as type; give them ours.
        if (problem.Type is null || problem.Type.StartsWith("https://tools.ietf.org", StringComparison.Ordinal))
        {
            switch (problem.Status)
            {
                case StatusCodes.Status400BadRequest:
                    problem.Type = ProblemTypes.Validation;
                    problem.Title = "The request is not valid.";
                    problem.Extensions.TryAdd("errorKind", "Validation");
                    break;
                case StatusCodes.Status504GatewayTimeout:
                    // The host's request-timeout backstop fired; the modules' own deadlines fire first.
                    problem.Type = ProblemTypes.Timeout;
                    problem.Title = "The request timed out.";
                    problem.Extensions.TryAdd("errorKind", "Timeout");
                    break;
                case StatusCodes.Status500InternalServerError:
                    problem.Type = ProblemTypes.Internal;
                    problem.Title = "An unexpected error occurred.";
                    problem.Detail = null;
                    problem.Extensions.TryAdd("errorKind", "Internal");
                    break;
            }
        }
    }
}
