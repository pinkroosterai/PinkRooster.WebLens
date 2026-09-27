using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PinkRooster.WebLens.Api.Tests;

/// <summary>The Valkey L2 behind HybridCache, with the real search module and a counting fake SearXNG.</summary>
public class ValkeyCacheTests
{
    private static WebLensFactory Host(ValkeyContainer valkey, DelegateHandler network)
    {
        var factory = new WebLensFactory { Network = network };
        factory.Settings["WebLens:Api:ValkeyConnectionString"] = valkey.ConnectionString;
        return factory;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SearchAsync(WebLensFactory factory, string query)
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsync("/v1/search", new StringContent($$"""{ "query": "{{query}}" }""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static DelegateHandler Searx() => new((request, _) => Task.FromResult(
        request.RequestUri!.AbsolutePath == "/config" ? Samples.Json("{}", HttpStatusCode.NotFound) : Samples.Json(Samples.SearxJson)));

    [Fact]
    public async Task A_second_host_finds_the_first_hosts_result_in_valkey()
    {
        await using var valkey = await ValkeyContainer.StartAsync();
        var network = Searx();
        await using var first = Host(valkey, network);
        await using var second = Host(valkey, network);

        var (firstStatus, firstBody) = await SearchAsync(first, "valkey shared");
        var (secondStatus, secondBody) = await SearchAsync(second, "valkey shared");

        Assert.Equal(HttpStatusCode.OK, firstStatus);
        Assert.Equal(HttpStatusCode.OK, secondStatus);
        Assert.False(firstBody.GetProperty("meta").GetProperty("cached").GetBoolean());
        Assert.True(secondBody.GetProperty("meta").GetProperty("cached").GetBoolean());
        Assert.Equal(1, network.CallsTo("/search"));
    }

    [Fact]
    public async Task Stopping_valkey_mid_run_leaves_requests_succeeding_and_fast()
    {
        await using var valkey = await ValkeyContainer.StartAsync();
        var network = Searx();
        await using var factory = Host(valkey, network);

        var (before, _) = await SearchAsync(factory, "before the outage");
        valkey.Stop();

        var clock = Stopwatch.StartNew();
        var during = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            during.Add((await SearchAsync(factory, $"during the outage {i}")).Status);
        }

        clock.Stop();
        var (repeat, repeatBody) = await SearchAsync(factory, "before the outage");

        Assert.Equal(HttpStatusCode.OK, before);
        Assert.All(during, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"three searches during the outage took {clock.Elapsed}");
        Assert.Equal(HttpStatusCode.OK, repeat);
        Assert.True(repeatBody.GetProperty("meta").GetProperty("cached").GetBoolean(), "L1 should still hold the entry from before the outage");
    }
}
