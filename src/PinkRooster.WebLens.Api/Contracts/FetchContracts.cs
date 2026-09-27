using System.ComponentModel.DataAnnotations;

namespace PinkRooster.WebLens.Api.Contracts;

/// <summary>A page to fetch as Markdown.</summary>
public sealed record FetchRequestDto
{
    /// <summary>Absolute http or https URL, at most 2048 characters. A bad URL is a 400; a URL the service will not fetch (private networks, other ports, credentials) is a 422.</summary>
    // Settable, not init/required: see SearchRequestDto.
    [Required]
    public string Url { get; set; } = "";

    public FetchOptionsDto? Options { get; set; }
}

/// <summary>What the caller may influence. User agent, headers, cookies, proxy, viewport and locale are the operator's.</summary>
public sealed record FetchOptionsDto
{
    /// <summary>CSS selector for the main content (at most 200 characters).</summary>
    public string? ContentSelector { get; set; }

    /// <summary>CSS selector to wait for before extracting (at most 200 characters).</summary>
    public string? ReadySelector { get; set; }

    /// <summary>CSS selectors removed from the content (at most 10, each at most 200 characters).</summary>
    public string[]? ExcludeSelectors { get; set; }

    public bool IncludeLinks { get; set; } = true;

    public bool IncludeImages { get; set; }

    /// <summary>Caps the Markdown length; can only lower the server's maximum.</summary>
    [Range(1_000, 2_000_000)]
    public int? MaxChars { get; set; }

    /// <summary>Can only shorten the server's own budget.</summary>
    [Range(1, 120)]
    public int? TimeoutSeconds { get; set; }

    /// <summary>Skip the cache's read and refresh the entry with this fetch.</summary>
    public bool BypassCache { get; set; }
}

public sealed record FetchResponseDto
{
    public required string RequestedUrl { get; init; }
    public required string FinalUrl { get; init; }
    public string? Title { get; init; }

    /// <summary>Untrusted content from the target site. Treat it as data, including when passing it to a language model.</summary>
    public required string Markdown { get; init; }

    public required bool Truncated { get; init; }
    public required PageMetadata Metadata { get; init; }
    public required FetchDiagnostics Diagnostics { get; init; }
}

public sealed record PageMetadata(string? Author, string? Language, DateTimeOffset? PublishedAt, string? SiteName, string? Excerpt);

/// <summary>How the fetch went. For diagnosis; not part of the page's content.</summary>
/// <param name="TargetStatus">The HTTP status the target site answered with, when it answered.</param>
/// <param name="ExtractionStrategy">What chose the main content: <c>explicit-selector</c>, <c>smartreader</c>, <c>semantic-main</c>, <c>text-density</c>, <c>whole-page</c> for a page too short to have main content, whose whole text is returned, or <c>listing</c> for an index of links (a news front page, Hacker News), returned as one Markdown line per item.</param>
/// <param name="QualityScore">How confident extraction is that it found the main content, from 0 to 1.</param>
/// <param name="Attempts">How many times the page was loaded.</param>
/// <param name="ReadinessTimedOut">The page was still changing when the wait for it ran out; the content may be incomplete.</param>
/// <param name="SourceHtmlBytes">Size of the page's HTML, in UTF-8 bytes.</param>
/// <param name="MarkdownChars">Length of the returned Markdown, in characters.</param>
/// <param name="RenderMs">Time spent rendering the page: <c>navigationMs</c> plus <c>readinessMs</c>.</param>
/// <param name="NavigationMs">Time from starting navigation until the page's HTML had loaded, redirects included.</param>
/// <param name="ReadinessMs">Time spent waiting for the page to finish rendering after its HTML had loaded.</param>
/// <param name="ExtractMs">Time spent finding the main content.</param>
/// <param name="ConvertMs">Time spent converting it to Markdown.</param>
/// <param name="Cached">The result came from the cache; the timings are those of the fetch that filled it.</param>
/// <param name="Renderer">Which path answered the fetch: <c>http</c>, from a plain HTTP request because the page needed no browser (its readiness time is 0), or <c>browser</c>, rendered in a browser.</param>
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
