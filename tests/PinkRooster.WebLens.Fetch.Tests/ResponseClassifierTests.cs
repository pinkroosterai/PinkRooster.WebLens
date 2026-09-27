namespace PinkRooster.WebLens.Fetch.Tests;

/// <summary>The status and content-type table.</summary>
public class ResponseClassifierTests
{
    private const string Html = "<html><body><article><p>Some ordinary content.</p></article></body></html>";

    [Theory]
    [InlineData(200, null, false)]
    [InlineData(204, null, false)]
    [InlineData(401, FetchErrorKind.TargetAccessDenied, false)]
    [InlineData(403, FetchErrorKind.TargetAccessDenied, false)]
    [InlineData(404, FetchErrorKind.TargetNotFound, false)]
    [InlineData(410, FetchErrorKind.TargetNotFound, false)]
    [InlineData(400, FetchErrorKind.TargetAccessDenied, false)]
    [InlineData(451, FetchErrorKind.TargetAccessDenied, false)]
    [InlineData(408, FetchErrorKind.TargetUnavailable, true)]
    [InlineData(429, FetchErrorKind.TargetUnavailable, true)]
    [InlineData(500, FetchErrorKind.TargetUnavailable, true)]
    [InlineData(502, FetchErrorKind.TargetUnavailable, true)]
    [InlineData(503, FetchErrorKind.TargetUnavailable, true)]
    [InlineData(504, FetchErrorKind.TargetUnavailable, true)]
    [InlineData(501, FetchErrorKind.TargetUnavailable, false)]
    public async Task Status_codes_map_to_the_table(int status, FetchErrorKind? expected, bool transient)
    {
        using var pipeline = Pipeline.Create();

        var verdict = (await pipeline.RunAsync(Pipeline.Page(Html, status: status))).Verdict;

        Assert.Equal(expected, verdict.Error);
        Assert.Equal(transient, verdict.Transient);
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/png")]
    [InlineData("application/json")]
    public async Task Only_html_proceeds(string contentType)
    {
        using var pipeline = Pipeline.Create();

        var verdict = (await pipeline.RunAsync(Pipeline.Page("", contentType: contentType))).Verdict;

        Assert.Equal(FetchErrorKind.UnsupportedContent, verdict.Error);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/xhtml+xml; charset=utf-8")]
    public void Html_and_xhtml_count_as_html(string contentType)
    {
        Assert.True(RenderedPage.IsHtmlType(contentType));
    }

    [Fact]
    public async Task Retry_after_in_seconds_is_carried()
    {
        using var pipeline = Pipeline.Create();

        var verdict = (await pipeline.RunAsync(Pipeline.Page(Html, status: 429, headers: new() { ["retry-after"] = "12" }))).Verdict;

        Assert.Equal(TimeSpan.FromSeconds(12), verdict.RetryAfter);
    }

    [Fact]
    public async Task Retry_after_as_an_http_date_is_relative_to_now()
    {
        using var pipeline = Pipeline.Create();
        var date = pipeline.Time.GetUtcNow().AddSeconds(30).ToString("r", System.Globalization.CultureInfo.InvariantCulture);

        var verdict = (await pipeline.RunAsync(Pipeline.Page(Html, status: 503, headers: new() { ["retry-after"] = date }))).Verdict;

        Assert.Equal(TimeSpan.FromSeconds(30), verdict.RetryAfter);
    }
}
