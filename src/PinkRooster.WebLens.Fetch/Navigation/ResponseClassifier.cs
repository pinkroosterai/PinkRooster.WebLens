using System.Globalization;
using AngleSharp.Dom;

namespace PinkRooster.WebLens.Fetch;

/// <summary>The verdict on one navigation. <see cref="Error"/> null means the page goes on to extraction.</summary>
internal sealed record Classification(FetchErrorKind? Error, bool Transient, string Message, TimeSpan? RetryAfter, BlockDetectionResult Block)
{
    /// <summary>The egress proxy refused the target: a security signal, logged at Warning.</summary>
    public bool ProxyDenied { get; init; }

    public static Classification Ok { get; } = new(null, false, "", null, BlockDetectionResult.None);
}

/// <summary>Classifies the main response, first decisive result wins: block detectors, then the HTTP status, then the content type.</summary>
internal sealed class ResponseClassifier(BlockDetectorChain detectors, TimeProvider time)
{
    public Classification Classify(RenderedPage page, IDocument? document)
    {
        // For plain http the refusal arrives as the proxy's own 407 response. Callers hear
        // "unavailable", as for any unreachable target, and never which address the name resolved to.
        if (page.Status == 407 && page.Headers.ContainsKey("x-smokescreen-error"))
        {
            return new Classification(FetchErrorKind.TargetUnavailable, false, "The target site could not be reached.", null, BlockDetectionResult.None) { ProxyDenied = true };
        }

        // Blocks come first, so a challenge page is never "extracted" into plausible-looking Markdown.
        if (document is not null)
        {
            var block = detectors.Detect(new BlockInput(page.Status, page.Headers, document));
            if (block.IsBlocked)
            {
                return block.Kind == BlockKind.Captcha
                    ? new Classification(FetchErrorKind.CaptchaRequired, false, "The target site asks for a CAPTCHA. The service does not solve CAPTCHAs.", null, block)
                    : new Classification(FetchErrorKind.BotChallenge, false, "The target site served a bot challenge. The service does not attempt to solve challenges.", null, block);
            }
        }

        if (ClassifyStatus(page) is { } byStatus)
        {
            return byStatus;
        }

        return page.IsHtml
            ? Classification.Ok
            : new Classification(FetchErrorKind.UnsupportedContent, false, "The target is not an HTML page.", null, BlockDetectionResult.None);
    }

    private Classification? ClassifyStatus(RenderedPage page) => page.Status switch
    {
        null or (>= 200 and < 300) => null,
        401 or 403 => Terminal(FetchErrorKind.TargetAccessDenied, "The target site refused access."),
        404 or 410 => Terminal(FetchErrorKind.TargetNotFound, "The target page does not exist."),
        408 or 429 or 500 or 502 or 503 or 504 => new Classification(
            FetchErrorKind.TargetUnavailable, true, "The target site is unavailable.", RetryAfter(page), BlockDetectionResult.None),
        >= 400 and < 500 => Terminal(FetchErrorKind.TargetAccessDenied, "The target site refused the request."),
        _ => Terminal(FetchErrorKind.TargetUnavailable, "The target site answered with an unusable status."),
    };

    private static Classification Terminal(FetchErrorKind kind, string message) => new(kind, false, message, null, BlockDetectionResult.None);

    /// <summary>Seconds or an HTTP-date; a date in the past means "now".</summary>
    private TimeSpan? RetryAfter(RenderedPage page)
    {
        if (!page.Headers.TryGetValue("retry-after", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParseExact(raw.Trim(), "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var delta = date - time.GetUtcNow();
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        return null;
    }
}
