using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Search;

internal enum AttemptOutcome
{
    Success,
    /// <summary>DNS, connect or TLS failure: the only kind that is retried on the same instance.</summary>
    ConnectFailure,
    /// <summary>Any other network failure (reset mid-response and so on).</summary>
    TransportFailure,
    Timeout,
    ServerError,
    RateLimited,
    /// <summary>403. Whether it means "JSON disabled" is decided by the caller, which knows what /config said.</summary>
    Forbidden,
    AccessDenied,
    Protocol,
    BadRequest,
    SameHostRedirect,
    ExternalRedirect,
}

internal sealed record AttemptResult(AttemptOutcome Outcome, int? StatusCode = null, SearxResponse? Body = null, TimeSpan? RetryAfter = null);

/// <summary>One HTTP attempt against one instance: streamed, size-capped, with its own timeout, and classified by status.</summary>
internal sealed class SearxTransport(IHttpClientFactory httpClients, IOptions<SearchOptions> options, TimeProvider time)
{
    public async Task<AttemptResult> SendAsync(SearchInstance instance, NormalizedQuery query, CancellationToken ct)
    {
        var transport = options.Value.Transport;

        // HttpClient.Timeout ends at the headers with ResponseHeadersRead, so this token is the one budget that also covers the body.
        using var deadline = new CancellationTokenSource(options.Value.Timeouts.Attempt, time);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        try
        {
            using var request = BuildRequest(instance, query, transport.Method);
            var client = httpClients.CreateClient(SearchServiceCollectionExtensions.HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            var status = (int)response.StatusCode;

            if (status is >= 300 and < 400)
            {
                return new AttemptResult(ClassifyRedirect(instance, response), status);
            }

            if (status != 200)
            {
                return new AttemptResult(ClassifyStatus(status), status, RetryAfter: response.Headers.RetryAfter is { } ra ? RetryAfterOf(ra) : null);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            {
                return new AttemptResult(AttemptOutcome.Protocol, status);
            }

            await using var body = await LimitedStream.OpenAsync(response, transport.MaxResponseBytes, attempt.Token);
            var parsed = await JsonSerializer.DeserializeAsync(body, SearxJsonContext.Default.SearxResponse, attempt.Token);
            return parsed is null
                ? new AttemptResult(AttemptOutcome.Protocol, status)
                : new AttemptResult(AttemptOutcome.Success, status, parsed);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new AttemptResult(AttemptOutcome.Timeout);
        }
        catch (HttpRequestException ex)
        {
            return new AttemptResult(IsConnectionEstablishment(ex) ? AttemptOutcome.ConnectFailure : AttemptOutcome.TransportFailure);
        }
        catch (IOException)
        {
            // A connection lost while the body streams surfaces as HttpIOException, an IOException rather than an HttpRequestException.
            return new AttemptResult(AttemptOutcome.TransportFailure, 200);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return new AttemptResult(AttemptOutcome.Protocol, 200);
        }
    }

    internal static AttemptOutcome ClassifyStatus(int status) => status switch
    {
        400 => AttemptOutcome.BadRequest,
        403 => AttemptOutcome.Forbidden,
        401 => AttemptOutcome.AccessDenied,
        429 => AttemptOutcome.RateLimited,
        408 or (>= 500 and <= 599) => AttemptOutcome.ServerError,
        _ => AttemptOutcome.Protocol,
    };

    private static AttemptOutcome ClassifyRedirect(SearchInstance instance, HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null)
        {
            return AttemptOutcome.Protocol;
        }

        var target = location.IsAbsoluteUri ? location : new Uri(instance.SearchUri, location);

        // Same host: the base URI is misconfigured (for example http answering with a redirect to https).
        // Different host: SearXNG answered an external bang (!!name) with a redirect to that engine, which the caller did not ask us to follow.
        return instance.SameHost(target) ? AttemptOutcome.SameHostRedirect : AttemptOutcome.ExternalRedirect;
    }

    private TimeSpan? RetryAfterOf(System.Net.Http.Headers.RetryConditionHeaderValue value)
    {
        if (value.Delta is { } delta)
        {
            return delta;
        }

        return value.Date is { } date ? date - time.GetUtcNow() : null;
    }

    private static bool IsConnectionEstablishment(HttpRequestException ex) => ex.HttpRequestError is
        HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError;

    private static HttpRequestMessage BuildRequest(SearchInstance instance, NormalizedQuery query, SearchHttpMethod method)
    {
        HttpRequestMessage request;
        if (method == SearchHttpMethod.Post)
        {
            request = new HttpRequestMessage(HttpMethod.Post, instance.SearchUri) { Content = new FormUrlEncodedContent(query.Fields) };
        }
        else
        {
            var qs = string.Join('&', query.Fields.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));
            request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder(instance.SearchUri) { Query = qs }.Uri);
        }

        request.Headers.Accept.ParseAdd("application/json");
        foreach (var (name, value) in instance.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }
}
