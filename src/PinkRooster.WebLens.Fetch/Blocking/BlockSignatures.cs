namespace PinkRooster.WebLens.Fetch;

/// <summary>
/// Versioned block signatures. Structural evidence (headers, widgets, challenge scripts) carries the
/// weight; words in the text are weak and never reach the threshold alone. Each signature set has positive and negative
/// fixtures in <c>Fetch.Tests</c>; change a weight only with a fixture that shows why. A CAPTCHA widget and its script
/// together stay below the threshold (0.6): a long article with a protected comment form is not a block.
/// </summary>
internal static class BlockSignatures
{
    public const string Version = "2026-09-25";

    private const string ShortPage = "1500";

    public static IReadOnlyList<BlockSignature> HttpStatus { get; } =
    [
        new("status-403", "unknown", BlockKind.BotChallenge, SignatureTarget.Status, "403", 0.3),
        new("status-429", "unknown", BlockKind.BotChallenge, SignatureTarget.Status, "429", 0.3),
        new("status-503", "unknown", BlockKind.BotChallenge, SignatureTarget.Status, "503", 0.3),
        new("title-just-a-moment", "unknown", BlockKind.BotChallenge, SignatureTarget.Title, "just a moment", 0.3),
        new("title-attention-required", "unknown", BlockKind.BotChallenge, SignatureTarget.Title, "attention required", 0.3),
        new("title-verify-human", "unknown", BlockKind.BotChallenge, SignatureTarget.Title, "verify you are human", 0.3),
        new("title-access-denied", "unknown", BlockKind.BotChallenge, SignatureTarget.Title, "access denied", 0.1),
        new("text-enable-javascript-cookies", "unknown", BlockKind.BotChallenge, SignatureTarget.Text, "enable javascript and cookies", 0.2),
        new("short-page", "unknown", BlockKind.BotChallenge, SignatureTarget.ShortText, ShortPage, 0.2),
    ];

    public static IReadOnlyList<BlockSignature> Cloudflare { get; } =
    [
        new("cf-mitigated-header", "cloudflare", BlockKind.BotChallenge, SignatureTarget.Header, "cf-mitigated: challenge", 1.0),
        new("cf-challenge-platform-script", "cloudflare", BlockKind.BotChallenge, SignatureTarget.ResourceUrl, "/cdn-cgi/challenge-platform/", 0.8),
        new("cf-challenge-form", "cloudflare", BlockKind.BotChallenge, SignatureTarget.Selector, "#challenge-form, #challenge-stage, #cf-challenge-running", 0.5),
        new("cf-title-just-a-moment", "cloudflare", BlockKind.BotChallenge, SignatureTarget.Title, "just a moment", 0.2),
        new("cf-text-ray-id", "cloudflare", BlockKind.BotChallenge, SignatureTarget.Text, "ray id", 0.1),
        new("cf-text-connection-secure", "cloudflare", BlockKind.BotChallenge, SignatureTarget.Text, "checking if the site connection is secure", 0.1),
    ];

    public static IReadOnlyList<BlockSignature> Captcha { get; } =
    [
        new("recaptcha-widget", "recaptcha", BlockKind.Captcha, SignatureTarget.Selector, ".g-recaptcha, iframe[src*='recaptcha']", 0.4),
        new("recaptcha-script", "recaptcha", BlockKind.Captcha, SignatureTarget.ResourceUrl, "/recaptcha/", 0.2),
        new("recaptcha-short-page", "recaptcha", BlockKind.Captcha, SignatureTarget.ShortText, ShortPage, 0.3),
        new("hcaptcha-widget", "hcaptcha", BlockKind.Captcha, SignatureTarget.Selector, ".h-captcha, iframe[src*='hcaptcha.com']", 0.4),
        new("hcaptcha-script", "hcaptcha", BlockKind.Captcha, SignatureTarget.ResourceUrl, "hcaptcha.com", 0.2),
        new("hcaptcha-short-page", "hcaptcha", BlockKind.Captcha, SignatureTarget.ShortText, ShortPage, 0.3),
        new("turnstile-widget", "cloudflare-turnstile", BlockKind.Captcha, SignatureTarget.Selector, ".cf-turnstile", 0.4),
        new("turnstile-script", "cloudflare-turnstile", BlockKind.Captcha, SignatureTarget.ResourceUrl, "challenges.cloudflare.com/turnstile", 0.2),
        new("turnstile-short-page", "cloudflare-turnstile", BlockKind.Captcha, SignatureTarget.ShortText, ShortPage, 0.3),
    ];

    public static IReadOnlyList<BlockSignature> Waf { get; } =
    [
        new("akamai-title", "akamai", BlockKind.BotChallenge, SignatureTarget.Title, "access denied", 0.3),
        new("akamai-reference", "akamai", BlockKind.BotChallenge, SignatureTarget.Text, "reference #18.", 0.4),
        new("akamai-server", "akamai", BlockKind.BotChallenge, SignatureTarget.Header, "server: akamaighost", 0.3),
        new("akamai-short-page", "akamai", BlockKind.BotChallenge, SignatureTarget.ShortText, ShortPage, 0.1),
        new("imperva-resource", "imperva", BlockKind.BotChallenge, SignatureTarget.ResourceUrl, "/_incapsula_resource", 0.8),
        new("imperva-incident", "imperva", BlockKind.BotChallenge, SignatureTarget.Text, "incapsula incident id", 0.5),
        new("datadome-captcha", "datadome", BlockKind.Captcha, SignatureTarget.ResourceUrl, "captcha-delivery.com", 0.9),
        new("datadome-header", "datadome", BlockKind.Captcha, SignatureTarget.Header, "x-datadome:", 0.3),
        new("perimeterx-captcha", "perimeterx", BlockKind.Captcha, SignatureTarget.Selector, "#px-captcha", 0.9),
    ];
}
