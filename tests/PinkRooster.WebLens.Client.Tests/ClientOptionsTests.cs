using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Client.Tests;

public class ClientOptionsTests
{
    [Fact]
    public void Valid_options_pass_validation()
    {
        var options = new WebLensClientOptions
        {
            BaseAddress = new Uri("https://weblens.local:8080/"),
            ApiKey = "wl_test_key_12345",
            Timeout = TimeSpan.FromSeconds(30),
            MaxRetries = 2,
            RetryInitialDelay = TimeSpan.FromMilliseconds(250),
        };

        options.Validate();

        var validator = new WebLensClientOptionsValidator();
        var result = validator.Validate(null, options);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_ApiKey_fails_validation(string? apiKey)
    {
        var options = new WebLensClientOptions
        {
            BaseAddress = new Uri("https://weblens.local/"),
            ApiKey = apiKey!,
        };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("ApiKey", ex.Message);

        var validator = new WebLensClientOptionsValidator();
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("ApiKey"));
    }

    [Fact]
    public void Null_BaseAddress_fails_validation()
    {
        var options = new WebLensClientOptions
        {
            BaseAddress = null,
            ApiKey = "key",
        };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("BaseAddress", ex.Message);

        var validator = new WebLensClientOptionsValidator();
        var result = validator.Validate(null, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Relative_BaseAddress_fails_validation()
    {
        var options = new WebLensClientOptions
        {
            BaseAddress = new Uri("/api/v1", UriKind.Relative),
            ApiKey = "key",
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Non_Http_BaseAddress_fails_validation()
    {
        var options = new WebLensClientOptions
        {
            BaseAddress = new Uri("ftp://example.com/"),
            ApiKey = "key",
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Non_positive_timeout_fails_validation()
    {
        var options = new WebLensClientOptions
        {
            BaseAddress = new Uri("https://example.com/"),
            ApiKey = "key",
            Timeout = TimeSpan.Zero,
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }
}
