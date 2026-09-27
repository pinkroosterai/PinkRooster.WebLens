namespace PinkRooster.WebLens.Api.Errors;

/// <summary>Stable URNs: clients switch on <c>type</c>, never on <c>title</c> or <c>detail</c>.</summary>
internal static class ProblemTypes
{
    public const string Prefix = "urn:weblens:problem:";

    public const string Validation = Prefix + "validation";
    public const string Unauthorized = Prefix + "unauthorized";
    public const string Forbidden = Prefix + "forbidden";
    public const string RateLimited = Prefix + "rate-limited";
    public const string SearchQueryUnsupported = Prefix + "search-query-unsupported";
    public const string SearchFailed = Prefix + "search-failed";
    public const string SearchUnavailable = Prefix + "search-unavailable";
    public const string TargetNotAllowed = Prefix + "target-not-allowed";
    public const string TargetNotFound = Prefix + "target-not-found";
    public const string TargetAccessDenied = Prefix + "target-access-denied";
    public const string BotChallenge = Prefix + "bot-challenge";
    public const string CaptchaRequired = Prefix + "captcha-required";
    public const string UnsupportedContent = Prefix + "unsupported-content";
    public const string ContentNotFound = Prefix + "content-not-found";
    public const string TargetUnavailable = Prefix + "target-unavailable";
    public const string CapacityExceeded = Prefix + "capacity-exceeded";
    public const string BrowserUnavailable = Prefix + "browser-unavailable";
    public const string Timeout = Prefix + "timeout";
    public const string Internal = Prefix + "internal";

    /// <summary>MCP only: a page read in parts changed between parts.</summary>
    public const string PageChanged = Prefix + "page-changed";
}
