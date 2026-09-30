using System.Net;
using PinkRooster.WebLens.Client.Exceptions;
using PinkRooster.WebLens.Client.Models;

namespace PinkRooster.WebLens.Client.Tests;

public class ExceptionMappingTests
{
    [Fact]
    public void RateLimited_urn_maps_to_WebLensRateLimitedException()
    {
        var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.Add("Retry-After", "12");
        var problem = new WebLensProblemDetails
        {
            Type = "urn:weblens:problem:rate-limited",
            Title = "Too Many Requests",
            Status = 429,
            Detail = "Rate limit exceeded.",
            RetryAfterSeconds = 12,
        };

        var ex = WebLensExceptionFactory.Create(response, problem, null);

        var rateLimitedEx = Assert.IsType<WebLensRateLimitedException>(ex);
        Assert.Equal(HttpStatusCode.TooManyRequests, rateLimitedEx.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(12), rateLimitedEx.RetryAfter);
        Assert.Equal("Rate limit exceeded.", rateLimitedEx.Message);
        Assert.Equal("urn:weblens:problem:rate-limited", rateLimitedEx.ProblemType);
    }

    [Theory]
    [InlineData("urn:weblens:problem:validation")]
    [InlineData("urn:weblens:problem:search-query-unsupported")]
    public void Validation_urns_map_to_WebLensValidationException(string type)
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest);
        var errors = new Dictionary<string, string[]>
        {
            ["query"] = ["The Query field is required."]
        };
        var problem = new WebLensProblemDetails
        {
            Type = type,
            Title = "Invalid request",
            Status = 400,
            Detail = "Validation error.",
            Errors = errors,
        };

        var ex = WebLensExceptionFactory.Create(response, problem, null);

        var valEx = Assert.IsType<WebLensValidationException>(ex);
        Assert.Equal(HttpStatusCode.BadRequest, valEx.StatusCode);
        Assert.True(valEx.Errors.ContainsKey("query"));
        Assert.Equal(["The Query field is required."], valEx.Errors["query"]);
    }

    [Theory]
    [InlineData("urn:weblens:problem:bot-challenge")]
    [InlineData("urn:weblens:problem:captcha-required")]
    public void Challenge_urns_map_to_WebLensChallengeException(string type)
    {
        var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
        var problem = new WebLensProblemDetails
        {
            Type = type,
            Title = "Challenge Required",
            Status = 422,
            Detail = "Bot challenge encountered."
        };

        var ex = WebLensExceptionFactory.Create(response, problem, null);

        var challengeEx = Assert.IsType<WebLensChallengeException>(ex);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, challengeEx.StatusCode);
        Assert.Equal("Bot challenge encountered.", challengeEx.Message);
    }

    [Fact]
    public void TargetNotAllowed_urn_maps_to_WebLensTargetNotAllowedException()
    {
        var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
        var problem = new WebLensProblemDetails
        {
            Type = "urn:weblens:problem:target-not-allowed",
            Title = "Target Not Allowed",
            Status = 422,
            Detail = "Target is a private network address."
        };

        var ex = WebLensExceptionFactory.Create(response, problem, null);

        var notAllowedEx = Assert.IsType<WebLensTargetNotAllowedException>(ex);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notAllowedEx.StatusCode);
        Assert.Equal("Target is a private network address.", notAllowedEx.Message);
    }

    [Fact]
    public void TargetNotFound_urn_maps_to_WebLensTargetNotFoundException()
    {
        var response = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
        var problem = new WebLensProblemDetails
        {
            Type = "urn:weblens:problem:target-not-found",
            Title = "Target Not Found",
            Status = 422,
            Detail = "Host not found."
        };

        var ex = WebLensExceptionFactory.Create(response, problem, null);

        var notFoundEx = Assert.IsType<WebLensTargetNotFoundException>(ex);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notFoundEx.StatusCode);
        Assert.Equal("Host not found.", notFoundEx.Message);
    }

    [Theory]
    [InlineData("urn:weblens:problem:search-unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("urn:weblens:problem:search-failed", HttpStatusCode.BadGateway)]
    [InlineData("urn:weblens:problem:target-unavailable", HttpStatusCode.BadGateway)]
    [InlineData("urn:weblens:problem:capacity-exceeded", HttpStatusCode.ServiceUnavailable)]
    [InlineData("urn:weblens:problem:browser-unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("urn:weblens:problem:timeout", HttpStatusCode.GatewayTimeout)]
    public void Unavailable_urns_map_to_WebLensUnavailableException(string type, HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Add("Retry-After", "5");
        var problem = new WebLensProblemDetails
        {
            Type = type,
            Title = "Service Unavailable",
            Status = (int)status,
            Detail = "Unavailable right now.",
            RetryAfterSeconds = 5,
        };

        var ex = WebLensExceptionFactory.Create(response, problem, null);

        var unavailEx = Assert.IsType<WebLensUnavailableException>(ex);
        Assert.Equal(status, unavailEx.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), unavailEx.RetryAfter);
        Assert.Equal("Unavailable right now.", unavailEx.Message);
    }

    [Fact]
    public void NonJson_proxy_error_maps_to_WebLensApiException_with_snippet()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadGateway);
        const string htmlError = "<html><body><h1>502 Bad Gateway</h1><p>Cloudflare reverse proxy error</p></body></html>";

        var ex = WebLensExceptionFactory.Create(response, null, htmlError);

        var apiEx = Assert.IsAssignableFrom<WebLensApiException>(ex);
        Assert.Equal(HttpStatusCode.BadGateway, apiEx.StatusCode);
        Assert.Contains("502", apiEx.Message);
        Assert.Equal(htmlError, apiEx.RawResponseBody);
    }

    [Fact]
    public void Unauthorized_urn_maps_to_WebLensApiException()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var problem = new WebLensProblemDetails
        {
            Type = "urn:weblens:problem:unauthorized",
            Title = "Unauthorized",
            Status = 401,
            Detail = "Missing or invalid API key."
        };

        var ex = WebLensExceptionFactory.Create(response, problem, null);

        var apiEx = Assert.IsType<WebLensApiException>(ex);
        Assert.Equal(HttpStatusCode.Unauthorized, apiEx.StatusCode);
        Assert.Equal("Missing or invalid API key.", apiEx.Message);
    }
}
