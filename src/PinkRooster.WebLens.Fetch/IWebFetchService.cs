namespace PinkRooster.WebLens.Fetch;

public interface IWebFetchService
{
    Task<FetchResult> FetchMarkdownAsync(FetchRequest request, CancellationToken ct);

    /// <summary>
    /// The cached result for this request, or null when there is none (or the request bypasses the cache). Never
    /// fetches. Validates the request first, like <see cref="FetchMarkdownAsync"/>.
    /// </summary>
    Task<FetchResult?> TryGetCachedAsync(FetchRequest request, CancellationToken ct);
}
