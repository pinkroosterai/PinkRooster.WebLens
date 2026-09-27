using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PinkRooster.WebLens.Api.Contracts;

public enum TimeRangeDto
{
    [JsonStringEnumMemberName("day")] Day,
    [JsonStringEnumMemberName("month")] Month,
    [JsonStringEnumMemberName("year")] Year,
}

public enum SafeSearchDto
{
    [JsonStringEnumMemberName("off")] Off,
    [JsonStringEnumMemberName("moderate")] Moderate,
    [JsonStringEnumMemberName("strict")] Strict,
}

/// <summary>A search request.</summary>
public sealed record SearchRequestDto
{
    /// <summary>The search text. May contain SearXNG bang syntax such as <c>!wp cats</c>; external bangs (<c>!!name</c>) are refused.</summary>
    // Settable, not init/required: System.Text.Json source generation passes init-only and required properties to a
    // constructor delegate as arguments and supplies default(T) for missing ones, which would overwrite the Page and
    // Limit defaults with 0. A missing query stays "" and fails [Required] instead. Its maximum length is the module's
    // (WebLens:Search:Query:MaxLength), so it is not repeated here.
    [Required]
    public string Query { get; set; } = "";

    /// <summary>SearXNG categories, for example <c>general</c> or <c>news</c>.</summary>
    [MaxLength(8)]
    public string[]? Categories { get; set; }

    /// <summary>Language code such as <c>en</c> or <c>en-GB</c>.</summary>
    public string? Language { get; set; }

    [Range(1, 10)]
    public int Page { get; set; } = 1;

    public TimeRangeDto? TimeRange { get; set; }

    public SafeSearchDto? SafeSearch { get; set; }

    /// <summary>Restrict the search to these engines (by name with underscores for spaces, or by shortcut). Inclusion only: there is no exclusion.</summary>
    [MaxLength(8)]
    public string[]? Engines { get; set; }

    /// <summary>Caps the number of results returned. Applied after retrieval, so it does not reduce upstream work.</summary>
    [Range(1, 50)]
    public int Limit { get; set; } = 10;
}

public sealed record SearchResponseDto
{
    public required string Query { get; init; }
    public required IReadOnlyList<SearchResultItem> Results { get; init; }

    /// <summary>Text answers only; other answer kinds are dropped and counted in <c>meta.droppedItems</c>.</summary>
    public required IReadOnlyList<AnswerItem> Answers { get; init; }

    public required IReadOnlyList<string> Suggestions { get; init; }
    public required IReadOnlyList<string> Corrections { get; init; }
    public required IReadOnlyList<InfoboxItem> Infoboxes { get; init; }
    public required SearchMeta Meta { get; init; }
}

/// <summary>One result. Its <c>score</c> is search-local ranking metadata, not comparable across queries.</summary>
public sealed record SearchResultItem(
    string Url,
    string Title,
    string? Snippet,
    string? Category,
    IReadOnlyList<string> Engines,
    double? Score,
    DateTimeOffset? PublishedAt,
    string? ThumbnailUrl,
    string? ImageUrl);

public sealed record AnswerItem(string Text);

public sealed record InfoboxItem(string? Title, string? Content, string? Url);

public sealed record EngineFailure(string Engine, string Reason);

public sealed record SearchMeta(
    int Page,
    int ResultCount,
    bool Partial,
    IReadOnlyList<EngineFailure> EngineFailures,
    int DroppedItems,
    long ElapsedMs,
    bool Cached);
