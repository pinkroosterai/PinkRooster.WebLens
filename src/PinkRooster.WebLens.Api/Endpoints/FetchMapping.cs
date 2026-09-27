using PinkRooster.WebLens.Api.Contracts;
using PinkRooster.WebLens.Fetch;

namespace PinkRooster.WebLens.Api.Endpoints;

/// <summary>Hand-written on purpose: this is where the public HTTP shape is decided.</summary>
internal static class FetchMapping
{
    public static FetchRequest ToModule(FetchRequestDto request) => new(request.Url)
    {
        ContentSelector = request.Options?.ContentSelector,
        ReadySelector = request.Options?.ReadySelector,
        ExcludeSelectors = request.Options?.ExcludeSelectors,
        IncludeLinks = request.Options?.IncludeLinks ?? true,
        IncludeImages = request.Options?.IncludeImages ?? false,
        MaxChars = request.Options?.MaxChars,
        Timeout = request.Options?.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
        BypassCache = request.Options?.BypassCache ?? false,
    };

    public static FetchResponseDto ToDto(FetchResult result) => new()
    {
        RequestedUrl = result.RequestedUrl.AbsoluteUri,
        FinalUrl = result.FinalUrl.AbsoluteUri,
        Title = result.Title,
        Markdown = result.Markdown,
        Truncated = result.Truncated,
        Metadata = new Contracts.PageMetadata(
            result.Metadata.Author, result.Metadata.Language, result.Metadata.PublishedAt, result.Metadata.SiteName, result.Metadata.Excerpt),
        Diagnostics = new Contracts.FetchDiagnostics(
            result.Diagnostics.TargetStatus,
            result.Diagnostics.ExtractionStrategy,
            result.Diagnostics.QualityScore,
            result.Diagnostics.Attempts,
            result.Diagnostics.ReadinessTimedOut,
            result.Diagnostics.SourceHtmlBytes,
            result.Diagnostics.MarkdownChars,
            result.Diagnostics.RenderMs,
            result.Diagnostics.ExtractMs,
            result.Diagnostics.ConvertMs,
            result.Diagnostics.Cached,
            result.Diagnostics.NavigationMs,
            result.Diagnostics.ReadinessMs,
            result.Diagnostics.Renderer),
    };

    /// <summary>
    /// The request field a module <see cref="FetchException.Field"/> refers to, named as the framework's own validation
    /// names fields (nested ones as <c>Options.Name</c>), so every 400 carries the same keys.
    /// </summary>
    public static string ToContractField(string? moduleField) => moduleField switch
    {
        null or nameof(FetchRequest.Url) => nameof(FetchRequestDto.Url),
        nameof(FetchRequest.Timeout) => $"{nameof(FetchRequestDto.Options)}.{nameof(FetchOptionsDto.TimeoutSeconds)}",
        _ => $"{nameof(FetchRequestDto.Options)}.{moduleField}",
    };
}
