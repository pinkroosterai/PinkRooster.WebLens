namespace PinkRooster.WebLens.Fetch;

public enum FetchErrorKind
{
    InvalidRequest,
    TargetNotAllowed,
    TargetNotFound,
    TargetAccessDenied,
    BotChallenge,
    CaptchaRequired,
    UnsupportedContent,
    ContentNotFound,
    TargetUnavailable,
    CapacityExceeded,
    BrowserUnavailable,
    Timeout,
    ConversionFailed,
}

/// <summary>
/// A fetch that did not produce Markdown. The message is safe to return to callers: it never contains the target URL,
/// proxy credentials, cookies, authorisation headers or page content.
/// </summary>
public class FetchException : Exception
{
    public FetchException(FetchErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public FetchErrorKind Kind { get; }

    /// <summary>For <see cref="FetchErrorKind.InvalidRequest"/>: the <see cref="FetchRequest"/> property at fault (<c>nameof</c>).</summary>
    public string? Field { get; init; }

    /// <summary>Status of the target's main document, when one was received.</summary>
    public int? TargetStatus { get; init; }

    /// <summary>When the caller may usefully try again: the target's Retry-After, a circuit's remaining time, or our capacity.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>For blocks: who served the challenge (for example <c>cloudflare</c>), when known.</summary>
    public string? Provider { get; init; }

    /// <summary>Navigation attempts made before the failure.</summary>
    public int Attempts { get; init; }

    /// <summary>For <see cref="FetchErrorKind.TargetNotAllowed"/>: which rule refused the URL. For logs only, never the response.</summary>
    public string? DenialReason { get; init; }
}
