using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Search;

public static class SearchServiceCollectionExtensions
{
    internal const string HttpClientName = "WebLens.Search";

    public static IServiceCollection AddWebLensSearch(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SearchOptions>().Bind(configuration).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SearchOptions>, SearchOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient(HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan) // one budget: the linked token in the transport
            .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = sp.GetRequiredService<IOptions<SearchOptions>>().Value.Transport.PooledConnectionLifetime,
            });

        services.AddSingleton<HealthRegistry>();
        services.AddSingleton<CapabilityCache>();
        services.AddSingleton<InstanceSelector>();
        services.AddSingleton<SearxTransport>();
        services.AddSingleton<ResponseMapper>();
        services.AddSingleton<SearchService>();
        services.AddHybridCache();
        services.AddSingleton<CachedSearchService>();

        // Outermost first: limit trimming, then the cache, then the search itself.
        services.AddSingleton<ISearchService>(sp => new LimitingSearchService(sp.GetRequiredService<CachedSearchService>()));

        // Tagged "capability": readiness is "either capability works".
        services.AddHealthChecks().AddCheck<SearchHealthCheck>(SearchHealthCheck.Name, tags: ["capability"]);
        return services;
    }
}
