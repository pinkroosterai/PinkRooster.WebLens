namespace PinkRooster.WebLens.Fetch.Tests;

public class FetchRequestNormalizerTests
{
    private static FetchException Invalid(FetchRequest request, Dictionary<string, string?>? settings = null)
    {
        using var pipeline = Pipeline.Create(settings);
        var ex = Assert.Throws<FetchException>(() => pipeline.Normalize(request));
        Assert.Equal(FetchErrorKind.InvalidRequest, ex.Kind);
        return ex;
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://example.org/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    public void Only_absolute_http_and_https_urls_are_accepted(string url)
    {
        Assert.Equal(nameof(FetchRequest.Url), Invalid(new FetchRequest(url)).Field);
    }

    [Fact]
    public void An_over_long_url_is_refused()
    {
        Assert.Equal(nameof(FetchRequest.Url), Invalid(new FetchRequest("https://example.org/" + new string('a', 2048))).Field);
    }

    [Theory]
    [InlineData("div[")]
    [InlineData("::nonsense(")]
    public void Invalid_selectors_are_refused_with_their_field(string selector)
    {
        Assert.Equal(nameof(FetchRequest.ContentSelector), Invalid(new FetchRequest("https://example.org/") { ContentSelector = selector }).Field);
        Assert.Equal(nameof(FetchRequest.ReadySelector), Invalid(new FetchRequest("https://example.org/") { ReadySelector = selector }).Field);
        Assert.Equal(nameof(FetchRequest.ExcludeSelectors), Invalid(new FetchRequest("https://example.org/") { ExcludeSelectors = ["nav", selector] }).Field);
    }

    [Fact]
    public void Each_exclude_selector_is_length_limited_and_their_number_capped()
    {
        Assert.Equal(nameof(FetchRequest.ExcludeSelectors), Invalid(new FetchRequest("https://example.org/") { ExcludeSelectors = [new string('a', 201)] }).Field);
        Assert.Equal(nameof(FetchRequest.ExcludeSelectors), Invalid(new FetchRequest("https://example.org/") { ExcludeSelectors = [.. Enumerable.Repeat("p", 11)] }).Field);
    }

    [Fact]
    public void Caller_limits_can_only_lower_the_servers()
    {
        using var pipeline = Pipeline.Create(new() { ["Limits:MaxMarkdownChars"] = "5000", ["Budget:OverallTimeout"] = "00:00:30" });

        var raised = pipeline.Normalize(new FetchRequest("https://example.org/") { MaxChars = 1_000_000, Timeout = TimeSpan.FromMinutes(5) });
        var lowered = pipeline.Normalize(new FetchRequest("https://example.org/") { MaxChars = 1000, Timeout = TimeSpan.FromSeconds(5) });

        Assert.Equal(5000, raised.MaxChars);
        Assert.Equal(TimeSpan.FromSeconds(30), raised.Budget);
        Assert.Equal(1000, lowered.MaxChars);
        Assert.Equal(TimeSpan.FromSeconds(5), lowered.Budget);
    }

    [Fact]
    public void The_origin_is_scheme_host_and_port_in_lower_case()
    {
        using var pipeline = Pipeline.Create();

        Assert.Equal("https://example.org:443", pipeline.Normalize(new FetchRequest("https://EXAMPLE.org/a?b=c#d")).Origin);
        Assert.Equal("http://example.org:8080", pipeline.Normalize(new FetchRequest("http://example.org:8080/")).Origin);
    }

    [Fact]
    public void A_site_profile_supplies_selectors_the_caller_did_not()
    {
        using var pipeline = Pipeline.Create(new()
        {
            ["SiteProfiles:0:Host"] = "docs.example.org",
            ["SiteProfiles:0:ContentSelector"] = "#content",
            ["SiteProfiles:0:ExcludeSelectors:0"] = ".ads",
        });

        var fromProfile = pipeline.Normalize(new FetchRequest("https://docs.example.org/page"));
        var fromCaller = pipeline.Normalize(new FetchRequest("https://docs.example.org/page") { ContentSelector = "main", ExcludeSelectors = ["aside"] });

        Assert.Equal("#content", fromProfile.ContentSelector);
        Assert.Equal("main", fromCaller.ContentSelector);
        Assert.Equal(["aside", ".ads"], fromCaller.ExcludeSelectors);
    }
}
