using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Search;

/// <summary>One logical search: one total deadline, sequential failover across instances, a hard cap on upstream attempts.</summary>
internal sealed partial class SearchService(
    IOptions<SearchOptions> options,
    InstanceSelector selector,
    SearxTransport transport,
    HealthRegistry health,
    CapabilityCache capabilities,
    ResponseMapper mapper,
    TimeProvider time,
    ILogger<SearchService> logger) : ISearchService
{
    private readonly SearchOptions _options = options.Value;

    /// <summary>No cache here: the answer is always none, after validating.</summary>
    public Task<SearchResponse?> TryGetCachedAsync(SearchQuery query, CancellationToken ct)
    {
        QueryNormalizer.Normalize(query, _options.Query.MaxLength);
        return Task.FromResult<SearchResponse?>(null);
    }

    public async Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var normalized = QueryNormalizer.Normalize(query, _options.Query.MaxLength);
        var attempts = new List<AttemptSummary>();

        // The module's own deadline. It fires before the host's request timeout so the caller gets a typed error.
        using var budget = new CancellationTokenSource(_options.Timeouts.Total, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);

        try
        {
            return await SearchWithinBudgetAsync(normalized, started, attempts, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new SearchException(SearchErrorKind.Timeout, "The search did not finish within its time budget.", attempts);
        }
    }

    private async Task<SearchResponse> SearchWithinBudgetAsync(NormalizedQuery query, long started, List<AttemptSummary> attempts, CancellationToken ct)
    {
        var selection = selector.Select(query);
        if (selection.Candidates.Count == 0)
        {
            if (selection.UnofferedField is { } field)
            {
                // Retrying will not help: no enabled instance offers what was asked for.
                throw new SearchException(SearchErrorKind.InvalidQuery, "No configured search instance offers a requested engine or category.") { Field = field };
            }

            throw NoEligible(attempts);
        }

        var maxAttempts = _options.Routing.MaxTotalAttempts;
        var rateLimited = 0;
        foreach (var instance in selection.Candidates)
        {
            if (attempts.Count >= maxAttempts)
            {
                break;
            }

            if (!health.TryBeginAttempt(instance.Name, out var isProbe))
            {
                continue;
            }

            AttemptResult result;
            try
            {
                result = await AttemptWithConnectRetryAsync(instance, query, attempts, maxAttempts, ct);
                await ReportOutcomeAsync(instance, result, isProbe, ct);
            }
            catch (Exception) when (isProbe)
            {
                // The caller left, the budget ran out, or something unexpected broke: none of it says anything about the
                // instance, and a probe permit that outlived its attempt would keep the instance out of rotation.
                health.ReleaseProbe(instance.Name);
                throw;
            }

            switch (result.Outcome)
            {
                case AttemptOutcome.Success:
                    var elapsed = (long)time.GetElapsedTime(started).TotalMilliseconds;
                    var response = mapper.Map(result.Body!, query, elapsed);
                    LogCompleted(instance.Name, attempts.Count, elapsed, response.Results.Count, response.Meta.Partial);
                    return response;
                case AttemptOutcome.BadRequest:
                    throw new SearchException(SearchErrorKind.InvalidQuery, "The search instance rejected the query.", attempts) { Field = nameof(SearchQuery.Text) };
                case AttemptOutcome.ExternalRedirect:
                    // An external bang (!!name): SearXNG wants to send the user to another site. Failing over would not change that.
                    throw new SearchException(SearchErrorKind.QueryUnsupported, "The query asks the search instance to redirect to an external site.", attempts);
                case AttemptOutcome.RateLimited:
                    rateLimited++;
                    break;
            }
        }

        if (attempts.Count == 0 || (rateLimited > 0 && rateLimited == attempts.Count))
        {
            throw NoEligible(attempts);
        }

        throw new SearchException(SearchErrorKind.AllAttemptsFailed, "Every attempted search instance failed.", attempts);
    }

    private async Task<AttemptResult> AttemptWithConnectRetryAsync(
        SearchInstance instance, NormalizedQuery query, List<AttemptSummary> attempts, int maxAttempts, CancellationToken ct)
    {
        AttemptResult result;
        var tries = 0;
        do
        {
            tries++;
            var attemptStarted = time.GetTimestamp();
            result = await transport.SendAsync(instance, query, ct);
            var elapsedMs = (long)time.GetElapsedTime(attemptStarted).TotalMilliseconds;

            attempts.Add(new AttemptSummary(instance.Name, result.Outcome.ToString(), result.StatusCode, attempts.Count + 1));
            LogAttempt(attempts.Count, instance.Name, result.Outcome, result.StatusCode, elapsedMs);

            // Only a connection-establishment failure is retried on the same instance; everything else fails over.
            var retry = result.Outcome == AttemptOutcome.ConnectFailure && tries == 1 && attempts.Count < maxAttempts;
            if (!retry)
            {
                break;
            }

            await BackoffAsync(ct);
        }
        while (true);

        return result;
    }

    private async Task BackoffAsync(CancellationToken ct)
    {
        var backoff = _options.Transport.ConnectRetryBackoff;
        if (backoff > TimeSpan.Zero)
        {
            // Jittered: 50%-150% of the configured delay.
            await Task.Delay(backoff * (0.5 + Random.Shared.NextDouble()), time, ct);
        }
    }

    /// <summary>The one place an attempt's outcome reaches the health registry.</summary>
    private async Task ReportOutcomeAsync(SearchInstance instance, AttemptResult result, bool isProbe, CancellationToken ct)
    {
        switch (result.Outcome)
        {
            case AttemptOutcome.Success:
                health.ReportSuccess(instance.Name);
                break;
            case AttemptOutcome.BadRequest or AttemptOutcome.ExternalRedirect:
                // About the query, not the instance.
                if (isProbe)
                {
                    health.ReleaseProbe(instance.Name);
                }

                break;
            case AttemptOutcome.ConnectFailure or AttemptOutcome.TransportFailure or AttemptOutcome.Timeout or AttemptOutcome.ServerError:
                health.ReportTransportFailure(instance.Name);
                break;
            case AttemptOutcome.RateLimited:
                health.ReportRateLimited(instance.Name, result.RetryAfter);
                break;
            case AttemptOutcome.Forbidden:
                await ClassifyForbiddenAsync(instance, ct);
                break;
            case AttemptOutcome.AccessDenied:
                health.ReportAccessDenied(instance.Name);
                break;
            default:
                health.ReportProtocolIncompatible(instance.Name);
                break;
        }
    }

    private async Task ClassifyForbiddenAsync(SearchInstance instance, CancellationToken ct)
    {
        // A 403 is "JSON disabled" only when the instance is known to be SearXNG: a reverse proxy or WAF can answer 403 too,
        // and SearXNG's own 403 page has no signature of its own. On this failure path we may wait for /config, for at most
        // one attempt's time; an unreadable /config leaves it a generic access denial.
        var snapshot = capabilities.GetFresh(instance.Name) ?? await capabilities.ProbeAsync(instance, ct);
        if (snapshot is { IsSearxng: true })
        {
            health.ReportJsonUnsupported(instance.Name);
        }
        else
        {
            health.ReportAccessDenied(instance.Name);
        }
    }

    private SearchException NoEligible(List<AttemptSummary> attempts)
    {
        var recovery = health.EarliestRecovery();
        TimeSpan? retryAfter = recovery is { } at ? at - time.GetUtcNow() : null;
        if (retryAfter is { } ra && ra < TimeSpan.Zero)
        {
            retryAfter = TimeSpan.Zero;
        }

        return new SearchException(SearchErrorKind.NoEligibleInstance, "No search instance is currently eligible.", attempts, retryAfter);
    }

    // Instance name and outcome only: never the query text or the instance URL.
    [LoggerMessage(Level = LogLevel.Debug, Message = "Search attempt {AttemptNumber} on {Instance}: {Outcome} (status {Status}) in {ElapsedMs} ms")]
    private partial void LogAttempt(int attemptNumber, string instance, AttemptOutcome outcome, int? status, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Search served by {Instance} after {Attempts} attempt(s) in {ElapsedMs} ms: {Results} result(s), partial {Partial}")]
    private partial void LogCompleted(string instance, int attempts, long elapsedMs, int results, bool partial);
}
