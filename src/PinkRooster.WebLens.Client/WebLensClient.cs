using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinkRooster.WebLens.Client.Exceptions;
using PinkRooster.WebLens.Client.Models;
using PinkRooster.WebLens.Client.Resilience;
using PinkRooster.WebLens.Client.Serialization;

namespace PinkRooster.WebLens.Client;

/// <summary>
/// Client implementation for communicating with the WebLens search and fetch service.
/// </summary>
public sealed class WebLensClient : IWebLensClient
{
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly string _apiKey;

    public WebLensClient(WebLensClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _apiKey = options.ApiKey;
        var innerHandler = new SocketsHttpHandler();
        var handler = WebLensResilience.CreateResilienceHandler(options, innerHandler);

        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = options.BaseAddress,
            Timeout = options.Timeout,
        };

        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
        _disposeHttpClient = true;
    }

    [ActivatorUtilitiesConstructor]
    public WebLensClient(HttpClient httpClient, IOptions<WebLensClientOptions> options)
        : this(httpClient, (options ?? throw new ArgumentNullException(nameof(options))).Value)
    {
    }

    public WebLensClient(HttpClient httpClient, WebLensClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _apiKey = options.ApiKey;
        _httpClient = httpClient;
        _disposeHttpClient = false;

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = options.BaseAddress;
        }

        if (!_httpClient.DefaultRequestHeaders.Contains("X-Api-Key"))
        {
            _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
        }
    }

    /// <inheritdoc />
    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/search")
        {
            Content = JsonContent.Create(request, WebLensJsonSerializerContext.Default.SearchRequest)
        };

        EnsureApiKeyHeader(httpRequest);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            await HandleErrorResponseAsync(response, cancellationToken).ConfigureAwait(false);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync(stream, WebLensJsonSerializerContext.Default.SearchResponse, cancellationToken).ConfigureAwait(false);

        return result ?? throw new WebLensApiException(response.StatusCode, null, "WebLens returned an empty search response.");
    }

    /// <inheritdoc />
    public async Task<FetchResponse> FetchAsync(FetchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/fetch")
        {
            Content = JsonContent.Create(request, WebLensJsonSerializerContext.Default.FetchRequest)
        };

        EnsureApiKeyHeader(httpRequest);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            await HandleErrorResponseAsync(response, cancellationToken).ConfigureAwait(false);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync(stream, WebLensJsonSerializerContext.Default.FetchResponse, cancellationToken).ConfigureAwait(false);

        return result ?? throw new WebLensApiException(response.StatusCode, null, "WebLens returned an empty fetch response.");
    }

    /// <inheritdoc />
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "health/live");
            EnsureApiKeyHeader(httpRequest);

            using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void EnsureApiKeyHeader(HttpRequestMessage request)
    {
        if (!request.Headers.Contains("X-Api-Key") && !_httpClient.DefaultRequestHeaders.Contains("X-Api-Key"))
        {
            request.Headers.Add("X-Api-Key", _apiKey);
        }
    }

    private static async Task HandleErrorResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? rawBody = null;
        WebLensProblemDetails? problem = null;

        try
        {
            rawBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(rawBody))
            {
                problem = JsonSerializer.Deserialize(rawBody, WebLensJsonSerializerContext.Default.WebLensProblemDetails);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // If body cannot be read or parsed as JSON, fall back to null problem and raw snippet.
        }

        throw WebLensExceptionFactory.Create(response, problem, rawBody);
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
