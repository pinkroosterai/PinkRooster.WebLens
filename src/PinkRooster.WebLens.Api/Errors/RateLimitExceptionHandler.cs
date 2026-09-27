using Microsoft.AspNetCore.Diagnostics;
using PinkRooster.WebLens.Api.Hosting;

namespace PinkRooster.WebLens.Api.Errors;

/// <summary>Writes a <see cref="RateLimitRefusal"/> as <c>429 rate-limited</c> with <c>Retry-After</c>. Logged where it is raised (<see cref="KeyRateLimiters.Acquire"/>).</summary>
internal sealed class RateLimitExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not RateLimitRefusal refusal)
        {
            return false;
        }

        var seconds = refusal.RetryAfterSeconds;
        await Problems.WriteAsync(
            problems,
            httpContext,
            StatusCodes.Status429TooManyRequests,
            ProblemTypes.RateLimited,
            "This API key has sent too many requests.",
            $"Retry after {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} second(s).",
            "RateLimited",
            seconds);
        return true;
    }
}
