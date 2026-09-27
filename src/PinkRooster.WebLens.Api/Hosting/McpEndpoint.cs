using Microsoft.AspNetCore.Builder;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PinkRooster.WebLens.Api.Endpoints;
using PinkRooster.WebLens.Api.Errors;
using PinkRooster.WebLens.Fetch;
using PinkRooster.WebLens.Search;

namespace PinkRooster.WebLens.Api.Hosting;

/// <summary>
/// The MCP front door: <c>search</c> and <c>fetch</c> as tools at <c>/mcp</c>, over stateless
/// Streamable HTTP, beside <c>/v1</c> and sharing its keys, scopes, per-key limits, cache and SSRF defence. A WebLens
/// refusal is a tool result flagged as an error, carrying its problem type; only a malformed MCP request is a protocol error.
/// </summary>
internal static class McpEndpoint
{
    public const string Path = "/mcp";

    private const string SdkLogCategory = "ModelContextProtocol";

    public static IServiceCollection AddWebLensMcp(this IServiceCollection services)
    {
        // The tools write the request summary's items onto the HTTP request they run in.
        services.AddHttpContextAccessor();

        // The SDK logs every JSON-RPC message in full at Trace (search text, target URLs, page text), which WebLens
        // never logs at any level. Its categories get a Debug floor for every provider that has rules of its own, unless the
        // operator set one for them; a provider-specific rule would otherwise win over a plain category rule.
        services.PostConfigure<LoggerFilterOptions>(filters =>
        {
            foreach (var provider in filters.Rules.Select(r => r.ProviderName).Append(null).Distinct().ToList())
            {
                if (!filters.Rules.Any(r => r.ProviderName == provider && r.CategoryName?.StartsWith(SdkLogCategory, StringComparison.Ordinal) == true))
                {
                    filters.Rules.Add(new LoggerFilterRule(provider, SdkLogCategory, LogLevel.Debug, null));
                }
            }
        });

        services.AddMcpServer(options => options.ServerInfo = new Implementation { Name = "weblens", Version = "1" })
            .WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateless)
            .AddAuthorizationFilters()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
            {
                try
                {
                    return await next(context, ct);
                }
                catch (SearchException ex)
                {
                    return ToolErrors.From(ex, context.Services!.GetRequiredService<ILogger<SearchExceptionHandler>>());
                }
                catch (FetchException ex)
                {
                    return ToolErrors.From(ex, context.Services!.GetRequiredService<ILogger<FetchExceptionHandler>>());
                }
                catch (ToolRefusal refusal)
                {
                    return refusal.Result;
                }
            }))
            .WithTools([typeof(SearchTool), typeof(FetchTool)]);
        return services;
    }

    /// <summary>Any valid key opens the endpoint; each tool then needs its own scope. The fetch backstop covers both tools.</summary>
    public static WebApplication MapWebLensMcp(this WebApplication app)
    {
        app.MapMcp(Path)
            .RequireAuthorization()
            .WithRequestTimeout(Scopes.Fetch)
            .ExcludeFromDescription();
        return app;
    }
}
