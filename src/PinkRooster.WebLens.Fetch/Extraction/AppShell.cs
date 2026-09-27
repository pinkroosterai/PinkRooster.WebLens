using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace PinkRooster.WebLens.Fetch;

/// <summary>
/// "App shell": HTML whose content is built by script, so a plain HTTP
/// response of it must not be answered without the browser. Judged on the HTTP-first response only.
/// </summary>
internal static partial class AppShell
{
    /// <summary>What a browser with script running never shows as text.</summary>
    private const string NeverShown = "script, style, noscript, template";

    /// <summary>
    /// Before extraction: the page has script and less visible text than a rendered page needs to count as ready. Covers an
    /// empty framework root and a hydration-only payload. A page that is merely short, with no script, is not a shell.
    /// </summary>
    public static bool IsShell(IDocument document, int minimumRenderedText)
    {
        if (document.QuerySelector("script") is null)
        {
            return false;
        }

        if (document.Body is not { } body)
        {
            return true;
        }

        var visible = (IElement)body.Clone();
        foreach (var hidden in visible.QuerySelectorAll(NeverShown).ToList())
        {
            hidden.Remove();
        }

        return TextMeasures.TextLength(visible) < minimumRenderedText;
    }

    /// <summary>After extraction: content that is mostly a request to enable JavaScript, not the page itself.</summary>
    public static bool AsksForScript(IElement content, int minimumContent) =>
        TextMeasures.TextLength(content) < minimumContent * 5 && AsksForJavaScript().IsMatch(content.TextContent);

    [GeneratedRegex(
        @"\b(enable|turn on|activate|requires?|need)\b.{0,40}\bjavascript\b|\bjavascript\b.{0,40}\b(required|disabled|is off|needed)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex AsksForJavaScript();
}
