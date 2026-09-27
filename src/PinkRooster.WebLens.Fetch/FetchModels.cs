namespace PinkRooster.WebLens.Fetch;

/// <summary>
/// What to fetch. The module validates every field (<see cref="FetchErrorKind.InvalidRequest"/> names the one at fault);
/// <see cref="Timeout"/> and <see cref="MaxChars"/> can only lower the server's limits.
/// </summary>
public sealed record FetchRequest(string Url)
{
    /// <summary>CSS selector for the main content; beats every heuristic.</summary>
    public string? ContentSelector { get; init; }

    /// <summary>CSS selector to wait for before the page counts as rendered.</summary>
    public string? ReadySelector { get; init; }

    /// <summary>CSS selectors removed from the chosen content.</summary>
    public IReadOnlyList<string>? ExcludeSelectors { get; init; }

    public bool IncludeLinks { get; init; } = true;
    public bool IncludeImages { get; init; }
    public int? MaxChars { get; init; }
    public TimeSpan? Timeout { get; init; }

    /// <summary>Skip the cache's read and refresh the entry with this fetch.</summary>
    public bool BypassCache { get; init; }
}

public sealed record PageMetadata(string? Author, string? Language, DateTimeOffset? PublishedAt, string? SiteName, string? Excerpt);

// RenderMs is NavigationMs (start to DOMContentLoaded, redirects included) plus ReadinessMs (the readiness strategy).
// The last three have defaults so results cached before they existed still read back.
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
    long NavigationMs = 0,
    long ReadinessMs = 0,
    string Renderer = FetchRenderers.Browser);

/// <summary>The values of <see cref="FetchDiagnostics.Renderer"/>.</summary>
public static class FetchRenderers
{
    public const string Browser = "browser";

    /// <summary>Answered from a plain HTTP request, without a browser.</summary>
    public const string Http = "http";
}

public sealed record FetchResult(
    Uri RequestedUrl,
    Uri FinalUrl,
    string? Title,
    string Markdown,
    bool Truncated,
    PageMetadata Metadata,
    FetchDiagnostics Diagnostics);
