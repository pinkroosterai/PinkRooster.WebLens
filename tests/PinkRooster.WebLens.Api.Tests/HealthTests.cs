using System.Net;
using System.Text;
using System.Text.Json;

namespace PinkRooster.WebLens.Api.Tests;

/// <summary>Health: live, ready ("either capability"), details (a valid key), from cached state only.</summary>
public class HealthTests
{
    private static WebLensFactory RealModules(out DelegateHandler network, HttpStatusCode searxStatus = HttpStatusCode.OK)
    {
        var handler = new DelegateHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/config" ? Samples.Json("{}", HttpStatusCode.NotFound) : Samples.Json(Samples.SearxJson, searxStatus)));
        network = handler;
        return new WebLensFactory { Network = handler };
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(WebLensFactory factory, string path, bool withKey = true)
    {
        factory.ApiKey = withKey ? WebLensFactory.AllScopesKey : null;
        using var client = factory.CreateClient();
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ready_when_search_works_even_though_the_browser_is_not_up()
    {
        await using var factory = RealModules(out _);

        var (status, body) = await GetAsync(factory, "/health/ready", withKey: false);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task Not_ready_when_no_capability_can_work()
    {
        await using var factory = RealModules(out _, HttpStatusCode.TooManyRequests);
        using (var client = factory.CreateClient())
        {
            // A 429 puts the only instance on cooldown; the browser was never launched.
            await client.PostAsync("/v1/search", new StringContent("""{ "query": "cats" }""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        }

        var (status, body) = await GetAsync(factory, "/health/ready", withKey: false);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("Unhealthy", body);
    }

    [Fact]
    public async Task Details_need_a_key_and_report_every_check_without_an_outbound_call()
    {
        await using var factory = RealModules(out var network);

        var (anonymous, _) = await GetAsync(factory, "/health/details", withKey: false);
        var callsBefore = network.Calls;
        var (status, body) = await GetAsync(factory, "/health/details");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(callsBefore, network.Calls);
        var checks = JsonDocument.Parse(body).RootElement.GetProperty("checks");
        Assert.Equal("Healthy", checks.GetProperty("search").GetProperty("status").GetString());
        Assert.Equal("Unknown", checks.GetProperty("search").GetProperty("data").GetProperty("a").GetString());
        Assert.Equal("Unhealthy", checks.GetProperty("fetch").GetProperty("status").GetString());
        Assert.Equal("Healthy", checks.GetProperty("cache").GetProperty("status").GetString());
        Assert.DoesNotContain("a.test", body);
    }

    [Fact]
    public async Task Live_needs_nothing()
    {
        await using var factory = RealModules(out _);

        var (status, _) = await GetAsync(factory, "/health/live", withKey: false);

        Assert.Equal(HttpStatusCode.OK, status);
    }
}
