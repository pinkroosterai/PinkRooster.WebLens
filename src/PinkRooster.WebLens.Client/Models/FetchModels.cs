namespace PinkRooster.WebLens.Client.Models;

/// <summary>A fetch request sent to WebLens.</summary>
public sealed record FetchRequest
{
    public FetchRequest()
    {
    }

    public FetchRequest(string url)
    {
        Url = url;
    }

    public string Url { get; set; } = string.Empty;

    public FetchOptions? Options { get; set; }
}

public sealed record FetchOptions
{
    public string? ContentSelector { get; set; }

    public string? ReadySelector { get; set; }

    public IReadOnlyList<string>? ExcludeSelectors { get; set; }

    public bool IncludeLinks { get; set; } = true;

    public bool IncludeImages { get; set; }

    public int? MaxChars { get; set; }

    public int? TimeoutSeconds { get; set; }

    public bool BypassCache { get; set; }
}

public sealed record FetchResponse
{
    public required string RequestedUrl { get; init; }
    public required string FinalUrl { get; init; }
    public string? Title { get; init; }
    public required string Markdown { get; init; }
    public required bool Truncated { get; init; }
    public required PageMetadata Metadata { get; init; }
    public required FetchDiagnostics Diagnostics { get; init; }
}

public sealed record PageMetadata(
    string? Author,
    string? Language,
    DateTimeOffset? PublishedAt,
    string? SiteName,
    string? Excerpt);

public sealed record FetchDiagnostics(
    int? TargetStatus,
    string ExtractionStrategy,
    double? QualityScore,
    int Attempts,
    bool ReadinessTimedOut,
    int SourceHtmlBytes,
    int MarkdownChars,
    long RenderMs,
    long ExtractMs,
    long ConvertMs,
    bool Cached,
    long NavigationMs,
    long ReadinessMs,
    string Renderer);
