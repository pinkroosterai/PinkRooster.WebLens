using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Fetch;

public static class FetchServiceCollectionExtensions
{
    public static IServiceCollection AddWebLensFetch(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FetchOptions>().Bind(configuration).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FetchOptions>, FetchOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IHostResolver, DnsHostResolver>();
        services.AddSingleton<UrlGuard>();
        services.AddSingleton<BrowserHost>();
        services.AddSingleton<ContextFactory>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, BrowserStartup>());

        services.AddSingleton<ReadinessStrategy>();
        services.AddSingleton<PageRenderer>();
        services.AddSingleton<HttpPageLoader>();

        // Order is not significant: the most confident block wins.
        services.AddSingleton<IBlockDetector, CloudflareDetector>();
        services.AddSingleton<IBlockDetector, CaptchaDomDetector>();
        services.AddSingleton<IBlockDetector, WafContentDetector>();
        services.AddSingleton<IBlockDetector, HttpStatusBlockDetector>();
        services.AddSingleton<BlockDetectorChain>();
        services.AddSingleton<ResponseClassifier>();

        // Order is significant: the first strategy whose candidate passes the quality check wins.
        services.AddSingleton<IExtractionStrategy, ExplicitSelectorExtractionStrategy>();
        services.AddSingleton<IExtractionStrategy, SmartReaderExtractionStrategy>();
        services.AddSingleton<IExtractionStrategy, SemanticMainExtractionStrategy>();
        services.AddSingleton<IExtractionStrategy, TextDensityFallbackStrategy>();
        services.AddSingleton<QualityScorer>();
        services.AddSingleton<ListingExtractor>();
        services.AddSingleton<ContentExtractor>();
        services.AddSingleton<MarkdownConverter>();

        services.AddSingleton<OriginRegistry>();
        services.AddSingleton<CapacityGate>();
        services.AddSingleton<WebFetchService>();
        services.AddHybridCache();
        services.AddSingleton<IWebFetchService, CachedWebFetchService>();

        services.AddSingleton<EgressProbe>();
        services.AddHostedService(sp => sp.GetRequiredService<EgressProbe>());

        // Tagged "capability": readiness is "either capability works".
        services.AddHealthChecks().AddCheck<FetchHealthCheck>(FetchHealthCheck.Name, tags: ["capability"]);
        return services;
    }
}
