using PinkRooster.WebLens.Client.Models;

namespace PinkRooster.WebLens.Client;

/// <summary>
/// Client interface for interacting with the WebLens search and fetch API.
/// </summary>
public interface IWebLensClient : IDisposable
{
    /// <summary>
    /// Executes a search query via <c>POST /v1/search</c>.
    /// </summary>
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a web page as Markdown via <c>POST /v1/fetch</c>.
    /// </summary>
    Task<FetchResponse> FetchAsync(FetchRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the health status of the WebLens host via <c>GET /health/live</c>.
    /// </summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default);
}
