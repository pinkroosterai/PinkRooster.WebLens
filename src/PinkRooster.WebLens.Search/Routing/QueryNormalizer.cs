using System.Globalization;
using System.Text.RegularExpressions;

namespace PinkRooster.WebLens.Search;

/// <summary>A validated query, compiled to the form fields SearXNG expects. Never logged: it holds the search text.</summary>
internal sealed record NormalizedQuery(
    IReadOnlyList<KeyValuePair<string, string>> Fields,
    IReadOnlyList<string> Engines,
    IReadOnlyList<string> Categories,
    string Text,
    int Page,
    int Limit);

/// <summary>The one place a search request's content rules live; the host maps a failure's <see cref="SearchException.Field"/> to its own field names.</summary>
internal static partial class QueryNormalizer
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,39}$")]
    private static partial Regex TokenRegex();

    [GeneratedRegex("^[A-Za-z0-9]{1,8}(-[A-Za-z0-9]{1,8}){0,3}$")]
    private static partial Regex LanguageRegex();

    public static NormalizedQuery Normalize(SearchQuery query, int maxLength)
    {
        var text = query.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw Invalid(nameof(SearchQuery.Text), "The query text is required.");
        }

        if (text.Length > maxLength)
        {
            throw Invalid(nameof(SearchQuery.Text), $"The query text is longer than {maxLength} characters.");
        }

        if (query.Page < 1)
        {
            throw Invalid(nameof(SearchQuery.Page), "Page must be 1 or greater.");
        }

        if (query.Limit < 1)
        {
            throw Invalid(nameof(SearchQuery.Limit), "Limit must be 1 or greater.");
        }

        var engines = Tokens(query.Engines, nameof(SearchQuery.Engines));
        var categories = Tokens(query.Categories, nameof(SearchQuery.Categories));

        string? language = null;
        if (!string.IsNullOrWhiteSpace(query.Language))
        {
            language = query.Language.Trim();
            if (!LanguageRegex().IsMatch(language))
            {
                throw Invalid(nameof(SearchQuery.Language), "Language must be a code such as 'en' or 'en-GB'.");
            }
        }

        // Engine selection has no REST parameter; it is compiled into the query as !engine tokens.
        var q = engines.Count == 0 ? text : string.Join(' ', engines.Select(e => "!" + e)) + " " + text;

        var fields = new List<KeyValuePair<string, string>>
        {
            new("q", q),
            new("format", "json"),
            new("pageno", query.Page.ToString(CultureInfo.InvariantCulture)),
        };

        if (categories.Count > 0)
        {
            fields.Add(new("categories", string.Join(',', categories)));
        }

        if (language is not null)
        {
            fields.Add(new("language", language));
        }

        if (query.TimeRange is { } range)
        {
            fields.Add(new("time_range", range switch
            {
                SearchTimeRange.Day => "day",
                SearchTimeRange.Month => "month",
                SearchTimeRange.Year => "year",
                _ => throw Invalid(nameof(SearchQuery.TimeRange), "Unknown time range."),
            }));
        }

        if (query.SafeSearch is { } safe)
        {
            fields.Add(new("safesearch", safe switch
            {
                SearchSafeSearch.Off => "0",
                SearchSafeSearch.Moderate => "1",
                SearchSafeSearch.Strict => "2",
                _ => throw Invalid(nameof(SearchQuery.SafeSearch), "Unknown safe-search level."),
            }));
        }

        return new NormalizedQuery(fields, engines, categories, text, query.Page, query.Limit);
    }

    private static List<string> Tokens(IReadOnlyList<string>? values, string field)
    {
        var result = new List<string>();
        if (values is null)
        {
            return result;
        }

        foreach (var raw in values)
        {
            var token = raw?.Trim() ?? "";

            // The token is compiled into the upstream search expression, so a permissive one would let a caller inject search syntax.
            if (!TokenRegex().IsMatch(token))
            {
                throw Invalid(field, "Every value must start with a letter or digit and contain only letters, digits, '_', '.' and '-' (at most 40 characters). " +
                    "Engines whose name has a space are written with an underscore or by their shortcut.");
            }

            if (!result.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(token);
            }
        }

        return result;
    }

    private static SearchException Invalid(string field, string message) => new(SearchErrorKind.InvalidQuery, message) { Field = field };
}
