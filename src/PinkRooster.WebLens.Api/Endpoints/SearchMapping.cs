using PinkRooster.WebLens.Api.Contracts;
using PinkRooster.WebLens.Search;

namespace PinkRooster.WebLens.Api.Endpoints;

/// <summary>Hand-written on purpose: this is where the public HTTP shape is decided.</summary>
internal static class SearchMapping
{
    public static SearchQuery ToModule(SearchRequestDto request) => new(request.Query)
    {
        Categories = request.Categories,
        Language = request.Language,
        Page = request.Page,
        TimeRange = request.TimeRange switch
        {
            TimeRangeDto.Day => SearchTimeRange.Day,
            TimeRangeDto.Month => SearchTimeRange.Month,
            TimeRangeDto.Year => SearchTimeRange.Year,
            _ => null,
        },
        SafeSearch = request.SafeSearch switch
        {
            SafeSearchDto.Off => SearchSafeSearch.Off,
            SafeSearchDto.Moderate => SearchSafeSearch.Moderate,
            SafeSearchDto.Strict => SearchSafeSearch.Strict,
            _ => null,
        },
        Engines = request.Engines,
        Limit = request.Limit,
    };

    public static SearchResponseDto ToDto(SearchResponse response) => new()
    {
        Query = response.Query,
        Results = [.. response.Results.Select(r => new SearchResultItem(
            r.Url, r.Title, r.Snippet, r.Category, r.Engines, r.Score, r.PublishedAt, r.ThumbnailUrl, r.ImageUrl))],
        Answers = [.. response.Answers.Select(a => new AnswerItem(a.Text))],
        Suggestions = response.Suggestions,
        Corrections = response.Corrections,
        Infoboxes = [.. response.Infoboxes.Select(i => new InfoboxItem(i.Title, i.Content, i.Url))],
        Meta = new Contracts.SearchMeta(
            response.Meta.Page,
            response.Meta.ResultCount,
            response.Meta.Partial,
            [.. response.Meta.EngineFailures.Select(f => new EngineFailure(f.Engine, f.Reason))],
            response.Meta.DroppedItems,
            response.Meta.ElapsedMs,
            response.Meta.Cached),
    };

    /// <summary>
    /// The request field a module <see cref="SearchException.Field"/> refers to, named as the framework's own validation names
    /// fields (the DTO property name), so every 400 carries the same keys.
    /// </summary>
    public static string ToContractField(string? moduleField) => moduleField switch
    {
        null or nameof(SearchQuery.Text) => nameof(SearchRequestDto.Query),
        _ => moduleField,
    };
}
