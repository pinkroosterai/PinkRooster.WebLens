using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using PinkRooster.WebLens.Client.Resilience;

namespace PinkRooster.WebLens.Client;

/// <summary>
/// Extension methods for configuring and registering <see cref="IWebLensClient"/> in an <see cref="IServiceCollection"/>.
/// </summary>
public static class WebLensClientServiceCollectionExtensions
{
    public const string HttpClientName = "WebLens.Client";

    /// <summary>
    /// Registers <see cref="IWebLensClient"/> and configured <see cref="HttpClient"/> with resilience handlers using the provided delegate.
    /// </summary>
    public static IHttpClientBuilder AddWebLensClient(this IServiceCollection services, Action<WebLensClientOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.Configure(configureOptions);
        return ConfigureWebLensClientServices(services);
    }

    /// <summary>
    /// Registers <see cref="IWebLensClient"/> and configured <see cref="HttpClient"/> with resilience handlers binding options from configuration.
    /// </summary>
    [RequiresUnreferencedCode("Binding options from IConfiguration is not AOT-safe without source generation. Use the Action<WebLensClientOptions> overload for NativeAOT.")]
    [RequiresDynamicCode("Binding options from IConfiguration is not AOT-safe without source generation. Use the Action<WebLensClientOptions> overload for NativeAOT.")]
    public static IHttpClientBuilder AddWebLensClient(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<WebLensClientOptions>().Bind(configuration).ValidateOnStart();
        return ConfigureWebLensClientServices(services);
    }

    private static IHttpClientBuilder ConfigureWebLensClientServices(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WebLensClientOptions>, WebLensClientOptionsValidator>());

        var builder = services.AddHttpClient<IWebLensClient, WebLensClient>(HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<WebLensClientOptions>>().Value;
            options.Validate();

            client.BaseAddress = options.BaseAddress;
            client.Timeout = options.Timeout;
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
            }
        });

        builder.AddResilienceHandler(WebLensResilience.ResilienceHandlerName, (pipelineBuilder, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<WebLensClientOptions>>().Value;
            if (options.EnableResilience && options.MaxRetries > 0)
            {
                pipelineBuilder.AddRetry(WebLensResilience.CreateRetryStrategyOptions(options));
            }
        });

        return builder;
    }
}
