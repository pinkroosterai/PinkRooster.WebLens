using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;

namespace PinkRooster.WebLens.Client.Resilience;

internal static class WebLensResilience
{
    public const string ResilienceHandlerName = "weblens-resilience";

    public static ResiliencePipeline<HttpResponseMessage> CreatePipeline(WebLensClientOptions options)
    {
        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(CreateRetryStrategyOptions(options))
            .Build();
    }

    public static HttpRetryStrategyOptions CreateRetryStrategyOptions(WebLensClientOptions options)
    {
        return new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = options.MaxRetries,
            BackoffType = DelayBackoffType.Exponential,
            Delay = options.RetryInitialDelay,
            UseJitter = true,
            DelayGenerator = static args =>
            {
                if (args.Outcome.Result?.Headers.RetryAfter is { } retryAfter)
                {
                    if (retryAfter.Delta is { } delta)
                    {
                        return ValueTask.FromResult<TimeSpan?>(delta);
                    }
                    if (retryAfter.Date is { } date)
                    {
                        var span = date - DateTimeOffset.UtcNow;
                        return ValueTask.FromResult<TimeSpan?>(span > TimeSpan.Zero ? span : TimeSpan.Zero);
                    }
                }
                return ValueTask.FromResult<TimeSpan?>(null);
            },
            ShouldHandle = static args =>
            {
                // Only retry on transient upstream and rate-limiting responses.
                // Refusals (400, 422, etc.) are non-transient and must never be retried.
                if (args.Outcome.Result is { } response)
                {
                    var statusCode = response.StatusCode;
                    var isTransient = statusCode is HttpStatusCode.BadGateway
                        or HttpStatusCode.ServiceUnavailable
                        or HttpStatusCode.GatewayTimeout
                        or (HttpStatusCode)429;

                    return ValueTask.FromResult(isTransient);
                }

                // Retry on transient network / socket / timeout exceptions when outcome is an exception.
                if (args.Outcome.Exception is HttpRequestException or TimeoutException)
                {
                    return ValueTask.FromResult(true);
                }

                return ValueTask.FromResult(false);
            }
        };
    }

    public static HttpMessageHandler CreateResilienceHandler(WebLensClientOptions options, HttpMessageHandler innerHandler)
    {
        if (!options.EnableResilience || options.MaxRetries <= 0)
        {
            return innerHandler;
        }

        var pipeline = CreatePipeline(options);
        return new ResilienceHandler(pipeline)
        {
            InnerHandler = innerHandler
        };
    }
}
