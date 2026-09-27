using System.Net;

namespace PinkRooster.WebLens.Api.Tests;

public class HealthLiveTests
{
    [Fact]
    public async Task Live_returns_200()
    {
        await using var factory = new WebLensFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
