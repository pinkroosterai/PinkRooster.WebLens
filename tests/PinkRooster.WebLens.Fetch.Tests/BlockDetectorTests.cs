namespace PinkRooster.WebLens.Fetch.Tests;

/// <summary>Each signature set with positive and negative fixtures.</summary>
public class BlockDetectorTests
{
    private const string LongText = "<p>" + "Plenty of ordinary article text about trams and rainbows. " + "</p>";

    private static async Task<Classification> ClassifyAsync(string html, int status = 200, Dictionary<string, string>? headers = null)
    {
        using var pipeline = Pipeline.Create();
        return (await pipeline.RunAsync(Pipeline.Page(html, status: status, headers: headers))).Verdict;
    }

    private static string Article(string extra) =>
        $"<html><head><title>News</title></head><body><article><h1>Story</h1>{string.Concat(Enumerable.Repeat(LongText, 40))}{extra}</article></body></html>";

    [Fact]
    public async Task The_cloudflare_mitigated_header_alone_is_a_challenge()
    {
        var verdict = await ClassifyAsync("<html><body><p>Hi</p></body></html>", 403, new() { ["cf-mitigated"] = "challenge" });

        Assert.Equal(FetchErrorKind.BotChallenge, verdict.Error);
        Assert.Equal("cloudflare", verdict.Block.Provider);
        Assert.Contains("cf-mitigated-header", verdict.Block.Evidence.Keys);
    }

    [Fact]
    public async Task An_article_that_discusses_captchas_is_not_blocked()
    {
        var verdict = await ClassifyAsync(Corpus.Read("article-about-captchas.html"));

        Assert.False(verdict.Block.IsBlocked);
        Assert.Null(verdict.Error);
    }

    [Fact]
    public async Task A_captcha_widget_on_a_long_article_is_a_comment_form_not_a_block()
    {
        var verdict = await ClassifyAsync(Article("<div class=\"g-recaptcha\" data-sitekey=\"x\"></div><script src=\"https://www.google.com/recaptcha/api.js\"></script>"));

        Assert.False(verdict.Block.IsBlocked);
    }

    [Fact]
    public async Task A_captcha_widget_on_an_otherwise_empty_page_is_captcha_required()
    {
        var verdict = await ClassifyAsync("<html><body><h1>Verify</h1><div class=\"cf-turnstile\"></div><script src=\"https://challenges.cloudflare.com/turnstile/v0/api.js\"></script></body></html>");

        Assert.Equal(FetchErrorKind.CaptchaRequired, verdict.Error);
        Assert.Equal("cloudflare-turnstile", verdict.Block.Provider);
    }

    [Fact]
    public async Task A_plain_403_is_access_denied_not_a_challenge()
    {
        var verdict = await ClassifyAsync("<html><head><title>403 Forbidden</title></head><body><h1>Forbidden</h1></body></html>", 403);

        Assert.False(verdict.Block.IsBlocked);
        Assert.Equal(FetchErrorKind.TargetAccessDenied, verdict.Error);
    }

    [Fact]
    public async Task A_plain_access_denied_403_is_still_access_denied()
    {
        var verdict = await ClassifyAsync("<html><head><title>Access Denied</title></head><body><h1>Access Denied</h1><p>Go away.</p></body></html>", 403);

        Assert.Equal(FetchErrorKind.TargetAccessDenied, verdict.Error);
    }

    [Fact]
    public async Task A_plain_429_is_rate_limiting_not_a_block()
    {
        var verdict = await ClassifyAsync("<html><head><title>Too Many Requests</title></head><body><p>Slow down.</p></body></html>", 429);

        Assert.False(verdict.Block.IsBlocked);
        Assert.Equal(FetchErrorKind.TargetUnavailable, verdict.Error);
        Assert.True(verdict.Transient);
    }

    [Fact]
    public async Task A_challenge_shaped_503_from_an_unknown_provider_is_a_challenge()
    {
        var verdict = await ClassifyAsync("<html><head><title>Just a moment...</title></head><body><p>Enable JavaScript and cookies to continue.</p></body></html>", 503);

        Assert.Equal(FetchErrorKind.BotChallenge, verdict.Error);
    }

    [Fact]
    public async Task A_lone_weak_text_signal_is_below_the_threshold()
    {
        var verdict = await ClassifyAsync("<html><head><title>Help</title></head><body><p>Checking if the site connection is secure. Ray ID questions? Ask us.</p></body></html>");

        Assert.False(verdict.Block.IsBlocked);
    }

    [Fact]
    public void The_signature_version_is_recorded()
    {
        Assert.Equal("2026-09-25", BlockSignatures.Version);
    }
}
