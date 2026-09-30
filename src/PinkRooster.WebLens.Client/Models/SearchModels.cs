using System.Text.Json.Serialization;

namespace PinkRooster.WebLens.Client.Models;

public enum TimeRange
{
    [JsonStringEnumMemberName("day")] Day,
    [JsonStringEnumMemberName("month")] Month,
    [JsonStringEnumMemberName("year")] Year,
}

public enum SafeSearch
{
    [JsonStringEnumMemberName("off")] Off,
    [JsonStringEnumMemberName("moderate")] Moderate,
    [JsonStringEnumMemberName("strict")] Strict,
}

/// <summary>A search request sent to WebLens.</summary>
public sealed record SearchRequest
{
    public SearchRequest()
    {
    }

    public SearchRequest(string query)
    {
        Query = query;
    }

    public string Query { get; set; } = string.Empty;

    public IReadOnlyList<string>? Categories { get; set; }

    public string? Language { get; set; }

    public int Page { get; set; } = 1;

    public TimeRange? TimeRange { get; set; }

    public SafeSearch? SafeSearch { get; set; }

    public IReadOnlyList<string>? Engines { get; set; }

    public int Limit { get; set; } = 10;
}

public sealed record SearchResponse
{
    public required string Query { get; init; }
    public required IReadOnlyList<SearchResultItem> Results { get; init; }
    public required IReadOnlyList<AnswerItem> Answers { get; init; }
    public required IReadOnlyList<string> Suggestions { get; init; }
    public required IReadOnlyList<string> Corrections { get; init; }
    public required IReadOnlyList<InfoboxItem> Infoboxes { get; init; }
    public required SearchMeta Meta { get; init; }
}

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
