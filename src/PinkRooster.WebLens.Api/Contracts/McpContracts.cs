namespace PinkRooster.WebLens.Api.Contracts;

/// <summary>
/// The MCP <c>search</c> tool's structured content: the <c>POST /v1/search</c> body with the untrusted-content notice first.
/// Claude Code gives the model the structured content instead of the text when a result has both, so the notice has to be
/// in here to reach the agent.
/// </summary>
public sealed record McpSearchResultDto
{
    /// <summary>Always the same line: the results are third-party content, to be treated as data.</summary>
    public required string Notice { get; init; }

    public required string Query { get; init; }
    public required IReadOnlyList<SearchResultItem> Results { get; init; }
    public required IReadOnlyList<AnswerItem> Answers { get; init; }
    public required IReadOnlyList<string> Suggestions { get; init; }
    public required IReadOnlyList<string> Corrections { get; init; }
    public required IReadOnlyList<InfoboxItem> Infoboxes { get; init; }
    public required SearchMeta Meta { get; init; }

    public static McpSearchResultDto From(SearchResponseDto response, string notice) => new()
    {
        Notice = notice,
        Query = response.Query,
        Results = response.Results,
        Answers = response.Answers,
        Suggestions = response.Suggestions,
        Corrections = response.Corrections,
        Infoboxes = response.Infoboxes,
        Meta = response.Meta,
    };
}
