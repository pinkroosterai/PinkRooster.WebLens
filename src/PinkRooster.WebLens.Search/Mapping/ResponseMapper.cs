using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Search;

/// <summary>Wire DTO to public model. Unusable items are dropped and counted, never allowed to fail the response.</summary>
internal sealed partial class ResponseMapper(IOptions<SearchOptions> options)
{
    private const int MaxListItems = 50;
    private const int MaxShortText = 200;

    // Inline tags vanish (so "wor<b>ld</b>" stays one word); every other tag becomes a space.
    [GeneratedRegex(@"</?(b|i|em|strong|span|a|mark|code|u|small|sub|sup)(\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineTagRegex();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public SearchResponse Map(SearxResponse wire, NormalizedQuery query, long elapsedMs)
    {
        var sanitization = options.Value.Sanitization;
        var dropped = 0;

        var results = new List<SearchResult>();
        foreach (var item in wire.Results ?? [])
        {
            if (MapResult(item, sanitization) is { } mapped)
            {
                results.Add(mapped);
            }
            else
            {
                dropped++;
            }
        }

        var answers = new List<SearchAnswer>();
        foreach (var element in wire.Answers ?? [])
        {
            if (AnswerText(element) is { } text)
            {
                answers.Add(new SearchAnswer(text));
            }
            else
            {
                dropped++;
            }
        }

        var infoboxes = new List<SearchInfobox>();
        foreach (var element in wire.Infoboxes ?? [])
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                infoboxes.Add(new SearchInfobox(
                    Text(GetString(element, "infobox"), MaxShortText),
                    Text(GetString(element, "content"), sanitization.MaxSnippet, stripHtml: true),
                    SafeUrl(GetString(element, "id"), sanitization.MaxUrl)));
            }
            else
            {
                dropped++;
            }
        }

        var failures = new List<SearchEngineFailure>();
        foreach (var element in wire.UnresponsiveEngines ?? [])
        {
            failures.Add(MapFailure(element));
        }

        return new SearchResponse(
            query.Text,
            results,
            answers,
            Strings(wire.Suggestions),
            Strings(wire.Corrections),
            infoboxes,
            new SearchMeta(query.Page, results.Count, failures.Count > 0, failures, dropped, elapsedMs, Cached: false));
    }

    private static SearchEngineFailure MapFailure(JsonElement element)
    {
        // Seen as ["engine", "reason"]; older versions may send a bare string.
        string? engine = null, reason = null;
        if (element.ValueKind == JsonValueKind.Array)
        {
            var items = element.EnumerateArray().ToList();
            engine = items.Count > 0 && items[0].ValueKind == JsonValueKind.String ? items[0].GetString() : null;
            reason = items.Count > 1 && items[1].ValueKind == JsonValueKind.String ? items[1].GetString() : null;
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            engine = element.GetString();
        }

        return new SearchEngineFailure(Text(engine, MaxShortText) ?? "unknown", Text(reason, MaxShortText) ?? "unknown");
    }

    private static SearchResult? MapResult(SearxResult item, SanitizationOptions sanitization)
    {
        var url = SafeUrl(item.Url, sanitization.MaxUrl);
        if (url is null)
        {
            return null;
        }

        var title = Text(item.Title, sanitization.MaxTitle, stripHtml: true);
        if (string.IsNullOrEmpty(title))
        {
            title = url;
        }

        var engines = (item.Engines ?? []).Concat(item.Engine is null ? [] : [item.Engine])
            .Select(e => Text(e, MaxShortText))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(MaxListItems)
            .ToList();

        return new SearchResult(
            url,
            title,
            Text(item.Content, sanitization.MaxSnippet, sanitization.StripSnippetHtml),
            Text(item.Category, MaxShortText),
            engines,
            Number(item.Score),
            // publishedDate is preferred over the deprecated pubdate.
            Date(item.PublishedDate) ?? Date(item.PubDate),
            SafeUrl(item.Thumbnail, sanitization.MaxUrl),
            SafeUrl(item.ImgSrc, sanitization.MaxUrl));
    }

    private string? AnswerText(JsonElement element)
    {
        var maxLength = options.Value.Sanitization.MaxSnippet;
        return element.ValueKind switch
        {
            JsonValueKind.String => Text(element.GetString(), maxLength, stripHtml: true),
            // Current SearXNG sends answer objects; only the plain-text kind is part of the public contract.
            JsonValueKind.Object when GetString(element, "answer") is { } answer => Text(answer, maxLength, stripHtml: true),
            _ => null,
        };
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> Strings(List<JsonElement>? elements) =>
        (elements ?? [])
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => Text(e.GetString(), MaxShortText))
            .OfType<string>()
            .Take(MaxListItems)
            .ToList();

    private static double? Number(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;

    private static DateTimeOffset? Date(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;

    private static string? SafeUrl(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > maxLength)
        {
            return null;
        }

        // Query strings are left exactly as SearXNG sent them: rewriting can break signed URLs.
        return Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.OriginalString : null;
    }

    private static string? Text(string? raw, int maxLength, bool stripHtml = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw;
        if (stripHtml)
        {
            text = WebUtility.HtmlDecode(HtmlTagRegex().Replace(InlineTagRegex().Replace(text, ""), " "));
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        var clean = WhitespaceRegex().Replace(builder.ToString(), " ").Trim();
        if (clean.Length == 0)
        {
            return null;
        }

        if (clean.Length <= maxLength)
        {
            return clean;
        }

        // Do not cut a surrogate pair in half.
        var end = char.IsHighSurrogate(clean[maxLength - 1]) ? maxLength - 1 : maxLength;
        return clean[..end].TrimEnd();
    }
}
