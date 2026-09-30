using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Client.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddWebLensClient_with_delegate_registers_IWebLensClient()
    {
        var services = new ServiceCollection();

        services.AddWebLensClient(options =>
        {
            options.BaseAddress = new Uri("https://weblens.local:5001/");
            options.ApiKey = "test_api_key_abc";
            options.Timeout = TimeSpan.FromSeconds(15);
            options.MaxRetries = 2;
        });

        using var provider = services.BuildServiceProvider();

        var client = provider.GetService<IWebLensClient>();
        Assert.NotNull(client);
        Assert.IsType<WebLensClient>(client);

        var options = provider.GetRequiredService<IOptions<WebLensClientOptions>>().Value;
        Assert.Equal(new Uri("https://weblens.local:5001/"), options.BaseAddress);
        Assert.Equal("test_api_key_abc", options.ApiKey);
        Assert.Equal(TimeSpan.FromSeconds(15), options.Timeout);
        Assert.Equal(2, options.MaxRetries);
    }

    [Fact]
    public void AddWebLensClient_with_configuration_binds_options()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["WebLens:Client:BaseAddress"] = "https://weblens.config:5002/",
            ["WebLens:Client:ApiKey"] = "config_key_xyz",
            ["WebLens:Client:Timeout"] = "00:00:25",
            ["WebLens:Client:MaxRetries"] = "4",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var services = new ServiceCollection();
        services.AddWebLensClient(configuration.GetSection("WebLens:Client"));

        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<WebLensClientOptions>>().Value;
        Assert.Equal(new Uri("https://weblens.config:5002/"), options.BaseAddress);
        Assert.Equal("config_key_xyz", options.ApiKey);
        Assert.Equal(TimeSpan.FromSeconds(25), options.Timeout);
        Assert.Equal(4, options.MaxRetries);

        var client = provider.GetService<IWebLensClient>();
        Assert.NotNull(client);
    }
}
