namespace PinkRooster.WebLens.Search;

public enum SearchTimeRange { Day, Month, Year }

public enum SearchSafeSearch { Off, Moderate, Strict }

/// <summary>What to search for. <see cref="Limit"/> is applied after retrieval and does not reduce upstream work.</summary>
public sealed record SearchQuery(string Text)
{
    public IReadOnlyList<string>? Categories { get; init; }
    public string? Language { get; init; }
    public int Page { get; init; } = 1;
    public SearchTimeRange? TimeRange { get; init; }
    public SearchSafeSearch? SafeSearch { get; init; }
    public IReadOnlyList<string>? Engines { get; init; }
    public int Limit { get; init; } = 10;
}

public sealed record SearchResult(
    string Url,
    string Title,
    string? Snippet,
    string? Category,
    IReadOnlyList<string> Engines,
    double? Score,
    DateTimeOffset? PublishedAt,
    string? ThumbnailUrl,
    string? ImageUrl);

public sealed record SearchAnswer(string Text);

public sealed record SearchInfobox(string? Title, string? Content, string? Url);

public sealed record SearchEngineFailure(string Engine, string Reason);

public sealed record SearchMeta(
    int Page,
    int ResultCount,
    bool Partial,
    IReadOnlyList<SearchEngineFailure> EngineFailures,
    int DroppedItems,
    long ElapsedMs,
    bool Cached);

public sealed record SearchResponse(
    string Query,
    IReadOnlyList<SearchResult> Results,
    IReadOnlyList<SearchAnswer> Answers,
    IReadOnlyList<string> Suggestions,
    IReadOnlyList<string> Corrections,
    IReadOnlyList<SearchInfobox> Infoboxes,
    SearchMeta Meta);
