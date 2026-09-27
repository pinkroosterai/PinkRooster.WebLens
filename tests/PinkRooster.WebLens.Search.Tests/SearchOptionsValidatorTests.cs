namespace PinkRooster.WebLens.Search.Tests;

public class SearchOptionsValidatorTests
{
    private static SearchOptions Valid()
    {
        var options = new SearchOptions();
        options.Instances.Add(new SearchInstanceOptions { Name = "a", BaseUri = "https://a.test" });
        return options;
    }

    private static string[] Failures(SearchOptions options)
    {
        var result = new SearchOptionsValidator().Validate(null, options);
        return result.Failures?.ToArray() ?? [];
    }

    [Fact]
    public void Accepts_a_minimal_valid_configuration() => Assert.Empty(Failures(Valid()));

    [Fact]
    public void Rejects_no_enabled_instance()
    {
        var options = Valid();
        options.Instances[0].Enabled = false;

        Assert.Contains(Failures(options), f => f.Contains("at least one enabled"));
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("ftp://a.test")]
    [InlineData("http://a.test")]
    [InlineData("https://a.test/?q=1")]
    [InlineData("https://a.test/#frag")]
    [InlineData("https://user:pw@a.test")]
    public void Rejects_unsafe_base_uris(string uri)
    {
        var options = Valid();
        options.Instances[0].BaseUri = uri;

        Assert.NotEmpty(Failures(options));
    }

    [Fact]
    public void Allows_http_only_when_insecure_http_is_enabled()
    {
        var options = Valid();
        options.Instances[0].BaseUri = "http://localhost:8080";
        options.Transport.AllowInsecureHttp = true;

        Assert.Empty(Failures(options));
    }

    [Fact]
    public void Rejects_duplicate_names_and_negative_priority()
    {
        var options = Valid();
        options.Instances.Add(new SearchInstanceOptions { Name = "A", BaseUri = "https://b.test", Priority = -1 });

        var failures = Failures(options);

        Assert.Contains(failures, f => f.Contains("duplicate"));
        Assert.Contains(failures, f => f.Contains("Priority"));
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("content-type")]
    [InlineData("bad header")]
    public void Rejects_reserved_or_invalid_header_names(string header)
    {
        var options = Valid();
        options.Instances[0].Headers[header] = "x";

        Assert.Contains(Failures(options), f => f.Contains("header"));
    }

    [Fact]
    public void Requires_the_attempt_timeout_to_be_shorter_than_the_total()
    {
        var options = Valid();
        options.Timeouts.Attempt = TimeSpan.FromSeconds(8);

        Assert.Contains(Failures(options), f => f.Contains("shorter than Total"));
    }

    [Fact]
    public void Rejects_non_positive_timeouts_and_attempt_caps()
    {
        var options = Valid();
        options.Timeouts.Total = TimeSpan.Zero;
        options.Routing.MaxTotalAttempts = 0;

        var failures = Failures(options);

        Assert.Contains(failures, f => f.Contains("positive"));
        Assert.Contains(failures, f => f.Contains("MaxTotalAttempts"));
    }
}
