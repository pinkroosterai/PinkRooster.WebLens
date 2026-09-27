using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using PinkRooster.WebLens.Api.Contracts;
using PinkRooster.WebLens.Api.Errors;
using StackExchange.Redis;

namespace PinkRooster.WebLens.Api.Hosting;

/// <summary>One extension method per concern, so <c>Program.cs</c> reads as the list of what the service does.</summary>
internal static class WebLensServices
{
    public static WebApplicationBuilder AddWebLensOptions(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<ApiOptions>().Bind(builder.Configuration.GetSection("WebLens:Api")).ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ApiOptions>, ApiOptionsValidator>();
        return builder;
    }

    public static WebApplicationBuilder AddWebLensJson(this WebApplicationBuilder builder)
    {
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));

        // By default a body that cannot be bound throws in Development and answers 400 elsewhere. Make it 400 everywhere so
        // the environment never changes the contract; UseStatusCodePages turns it into problem+json.
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
        builder.Services.AddValidation();
        return builder;
    }

    public static WebApplicationBuilder AddWebLensProblemDetails(this WebApplicationBuilder builder)
    {
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = Problems.Customize);

        // In this order, in front of the framework default. Each handles only its own exception type.
        builder.Services.AddExceptionHandler<ClientDisconnectedExceptionHandler>();
        builder.Services.AddExceptionHandler<RateLimitExceptionHandler>();
        builder.Services.AddExceptionHandler<SearchExceptionHandler>();
        builder.Services.AddExceptionHandler<FetchExceptionHandler>();
        return builder;
    }

    /// <summary>The <c>ApiKey</c> scheme and one policy per scope.</summary>
    public static WebApplicationBuilder AddWebLensAuth(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ApiKeyStore>();
        builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Scopes.Search, policy => policy.RequireAuthenticatedUser().RequireClaim(Scopes.ClaimType, Scopes.Search))
            .AddPolicy(Scopes.Fetch, policy => policy.RequireAuthenticatedUser().RequireClaim(Scopes.ClaimType, Scopes.Fetch));
        return builder;
    }

    /// <summary>The host's backstops, above the modules' own budgets, so the innermost deadline fires first.</summary>
    public static WebApplicationBuilder AddWebLensRequestTimeouts(this WebApplicationBuilder builder)
    {
        builder.Services.AddRequestTimeouts();
        builder.Services.AddOptions<Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutOptions>()
            .Configure<IOptions<ApiOptions>>((timeouts, api) =>
            {
                timeouts.AddPolicy(Scopes.Search, new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
                {
                    Timeout = api.Value.SearchRequestTimeout,
                    TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
                });
                timeouts.AddPolicy(Scopes.Fetch, new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
                {
                    Timeout = api.Value.FetchRequestTimeout,
                    TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
                });
            });
        return builder;
    }

    /// <summary>
    /// L1 in process always; L2 on Valkey when <c>WebLens:Api:ValkeyConnectionString</c> is set. Decided when
    /// the container resolves it, from the validated options, not while the builder runs: settings applied at build time
    /// (tests, and any late configuration source) must count. Without Valkey the L2 slot holds a
    /// <see cref="MemoryDistributedCache"/>, which HybridCache ignores as a second level.
    /// A Valkey outage must not fail requests or make them slow: HybridCache waits out the client's timeouts on every read
    /// and write, so they are short unless the connection string sets its own.
    /// </summary>
    public static WebApplicationBuilder AddWebLensCache(this WebApplicationBuilder builder)
    {
        builder.Services.AddHybridCache(options =>
        {
            options.MaximumPayloadBytes = 4 * 1024 * 1024;
            options.MaximumKeyLength = 256;
        });

        builder.Services.AddSingleton<IDistributedCache>(services =>
        {
            var connection = services.GetRequiredService<IOptions<ApiOptions>>().Value.ValkeyConnectionString;
            return string.IsNullOrWhiteSpace(connection)
                ? new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()))
                : new RedisCache(Options.Create(new RedisCacheOptions { ConfigurationOptions = ValkeyConfiguration(connection), InstanceName = "weblens:" }));
        });

        return builder;
    }

    internal static ConfigurationOptions ValkeyConfiguration(string connection)
    {
        var configuration = ConfigurationOptions.Parse(connection);
        configuration.AbortOnConnectFail = false;
        if (!connection.Contains("connectTimeout", StringComparison.OrdinalIgnoreCase))
        {
            configuration.ConnectTimeout = 1000;
        }

        if (!connection.Contains("asyncTimeout", StringComparison.OrdinalIgnoreCase))
        {
            configuration.AsyncTimeout = 250;
        }

        if (!connection.Contains("syncTimeout", StringComparison.OrdinalIgnoreCase))
        {
            configuration.SyncTimeout = 250;
        }

        return configuration;
    }

    /// <summary>
    /// Kestrel limits: both request types are small JSON bodies. Forwarded headers only from listed proxies.
    /// Shutdown waits longer than a fetch may run, so a running fetch finishes or times out cleanly.
    /// </summary>
    public static WebApplicationBuilder AddWebLensServer(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = 64 * 1024;
            kestrel.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            kestrel.Limits.MaxRequestHeaderCount = 64;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            kestrel.AddServerHeader = false;
        });

        builder.Services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ApiOptions>>((forwarded, api) =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarded.KnownIPNetworks.Clear();
                forwarded.KnownProxies.Clear();
                foreach (var proxy in api.Value.KnownProxies)
                {
                    forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
                }
            });

        builder.Services.Configure<HostOptions>(host =>
        {
            var overall = builder.Configuration.GetValue("WebLens:Fetch:Budget:OverallTimeout", TimeSpan.FromSeconds(30));
            host.ShutdownTimeout = overall + TimeSpan.FromSeconds(5);
        });
        return builder;
    }
}
