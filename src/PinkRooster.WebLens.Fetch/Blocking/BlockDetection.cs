using AngleSharp.Dom;

namespace PinkRooster.WebLens.Fetch;

internal enum BlockKind { None, BotChallenge, Captcha }

/// <summary>Evidence, not a bare boolean. <see cref="Evidence"/> names the signatures that matched.</summary>
internal sealed record BlockDetectionResult(
    bool IsBlocked,
    BlockKind Kind,
    string? Provider,
    double Confidence,
    IReadOnlyDictionary<string, string> Evidence)
{
    public static BlockDetectionResult None { get; } = new(false, BlockKind.None, null, 0, new Dictionary<string, string>());
}

/// <summary>What a detector looks at: the main response and the parsed page, before any clean-up.</summary>
internal sealed record BlockInput(int? Status, IReadOnlyDictionary<string, string> Headers, IDocument Document)
{
    private string? _text;

    /// <summary>Visible-ish text of the body with whitespace collapsed; computed once.</summary>
    public string Text => _text ??= string.Join(' ', (Document.Body?.TextContent ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public string Title => Document.Title ?? "";

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

internal interface IBlockDetector
{
    BlockDetectionResult Detect(BlockInput input);
}

/// <summary>Where a signature looks.</summary>
internal enum SignatureTarget
{
    /// <summary><c>Pattern</c> is <c>name: value-substring</c>; an empty value matches any value.</summary>
    Header,

    /// <summary><c>Pattern</c> is a CSS selector that must match at least one element.</summary>
    Selector,

    /// <summary>Substring of any <c>script[src]</c> or <c>iframe[src]</c>.</summary>
    ResourceUrl,

    /// <summary>Case-insensitive substring of the document title.</summary>
    Title,

    /// <summary>Case-insensitive substring of the body text. A weak signal: articles can discuss these words.</summary>
    Text,

    /// <summary>Main-response status code, as a number.</summary>
    Status,

    /// <summary>The body text is shorter than <c>Pattern</c> characters: the page has nothing else to show.</summary>
    ShortText,
}

/// <summary>One piece of evidence. Weights of the matching signatures for a provider add up to the confidence.</summary>
internal sealed record BlockSignature(string Id, string Provider, BlockKind Kind, SignatureTarget Target, string Pattern, double Weight)
{
    public bool Matches(BlockInput input) => Target switch
    {
        SignatureTarget.Header => MatchesHeader(input),
        SignatureTarget.Selector => input.Document.QuerySelector(Pattern) is not null,
        SignatureTarget.ResourceUrl => input.Document.QuerySelectorAll("script[src], iframe[src]")
            .Any(e => e.GetAttribute("src")!.Contains(Pattern, StringComparison.OrdinalIgnoreCase)),
        SignatureTarget.Title => input.Title.Contains(Pattern, StringComparison.OrdinalIgnoreCase),
        SignatureTarget.Text => input.Text.Contains(Pattern, StringComparison.OrdinalIgnoreCase),
        SignatureTarget.Status => input.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture) == Pattern,
        SignatureTarget.ShortText => input.Text.Length < int.Parse(Pattern, System.Globalization.CultureInfo.InvariantCulture),
        _ => false,
    };

    private bool MatchesHeader(BlockInput input)
    {
        var separator = Pattern.IndexOf(':', StringComparison.Ordinal);
        var name = Pattern[..separator].Trim();
        var value = Pattern[(separator + 1)..].Trim();
        return input.Header(name) is { } actual && actual.Contains(value, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Scores a set of signatures per provider and reports the strongest provider over the threshold. Signatures are data
/// (<see cref="BlockSignatures"/>) so they can change without touching detection logic.
/// </summary>
internal abstract class SignatureBlockDetector(IReadOnlyList<BlockSignature> signatures) : IBlockDetector
{
    /// <summary>Below this a page is not blocked: a lone weak signal (a word in an article) must never be enough.</summary>
    public const double Threshold = 0.7;

    public BlockDetectionResult Detect(BlockInput input)
    {
        BlockDetectionResult best = BlockDetectionResult.None;
        foreach (var group in signatures.GroupBy(s => (s.Provider, s.Kind)))
        {
            var evidence = new Dictionary<string, string>();
            var confidence = 0.0;
            foreach (var signature in group.Where(s => s.Matches(input)))
            {
                confidence += signature.Weight;
                evidence[signature.Id] = signature.Target.ToString();
            }

            confidence = Math.Min(confidence, 1.0);
            if (confidence >= Threshold && confidence > best.Confidence)
            {
                best = new BlockDetectionResult(true, group.Key.Kind, group.Key.Provider, confidence, evidence);
            }
        }

        return best;
    }
}

/// <summary>A challenge-shaped 403/429/503 from an unknown provider. A plain 429 without a challenge page is rate limiting, not a block.</summary>
internal sealed class HttpStatusBlockDetector() : SignatureBlockDetector(BlockSignatures.HttpStatus);

/// <summary>Cloudflare's managed challenge; <c>cf-mitigated: challenge</c> is an explicit signal on its own.</summary>
internal sealed class CloudflareDetector() : SignatureBlockDetector(BlockSignatures.Cloudflare);

/// <summary>A CAPTCHA widget on a page with little else on it. A widget on a long article (a comment form) is not a block.</summary>
internal sealed class CaptchaDomDetector() : SignatureBlockDetector(BlockSignatures.Captcha);

/// <summary>WAF block and challenge pages: Akamai, Imperva, DataDome, PerimeterX.</summary>
internal sealed class WafContentDetector() : SignatureBlockDetector(BlockSignatures.Waf);

/// <summary>Runs every detector; the most confident block wins.</summary>
internal sealed class BlockDetectorChain(IEnumerable<IBlockDetector> detectors)
{
    private readonly IReadOnlyList<IBlockDetector> _detectors = [.. detectors];

    public BlockDetectionResult Detect(BlockInput input)
    {
        var best = BlockDetectionResult.None;
        foreach (var detector in _detectors)
        {
            var result = detector.Detect(input);
            if (result.IsBlocked && result.Confidence > best.Confidence)
            {
                best = result;
            }
        }

        return best;
    }
}
