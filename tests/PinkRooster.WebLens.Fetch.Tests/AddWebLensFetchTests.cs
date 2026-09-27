using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PinkRooster.WebLens.Fetch.Tests;

public class AddWebLensFetchTests
{
    [Fact]
    public void Registers_the_fetch_service()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnvironment());
        services.AddWebLensFetch(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Security:RequireEgressProxy"] = "false" }).Build());

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IWebFetchService>());
    }
}
