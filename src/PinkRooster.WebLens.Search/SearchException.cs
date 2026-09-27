namespace PinkRooster.WebLens.Search;

public enum SearchErrorKind { InvalidQuery, QueryUnsupported, NoEligibleInstance, AllAttemptsFailed, Timeout }

/// <summary>Why one upstream attempt failed. For logs and diagnostics; never reaches a response body.</summary>
public sealed record AttemptSummary(string Instance, string FailureClass, int? StatusCode, int AttemptNumber);

public class SearchException : Exception
{
    public SearchException(SearchErrorKind kind, string message, IReadOnlyList<AttemptSummary>? attempts = null, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Attempts = attempts ?? [];
        RetryAfter = retryAfter;
    }

    public SearchErrorKind Kind { get; }

    /// <summary>For <see cref="SearchErrorKind.InvalidQuery"/>: the <see cref="SearchQuery"/> property at fault (<c>nameof</c>), when one is.</summary>
    public string? Field { get; init; }

    /// <summary>Set for <see cref="SearchErrorKind.AllAttemptsFailed"/> and <see cref="SearchErrorKind.NoEligibleInstance"/>.</summary>
    public IReadOnlyList<AttemptSummary> Attempts { get; }

    public TimeSpan? RetryAfter { get; }
}
