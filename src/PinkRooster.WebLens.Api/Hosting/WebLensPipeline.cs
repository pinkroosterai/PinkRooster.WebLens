using PinkRooster.WebLens.Api.Endpoints;

namespace PinkRooster.WebLens.Api.Hosting;

/// <summary>Middleware order is behaviour, and the <c>/v1</c> wiring.</summary>
internal static class WebLensPipeline
{
    public static WebApplication UseWebLensPipeline(this WebApplication app)
    {
        // 0. The per-request summary line sits outside everything, so it logs the status the client actually got.
        app.UseWebLensRequestSummary();

        // 1. Only when listed proxies exist; never trust unlisted ones.
        if (app.Configuration.GetSection("WebLens:Api:KnownProxies").GetChildren().Any())
        {
            app.UseForwardedHeaders();
        }

        // 2. Outermost, so every later failure becomes a problem response; status-code pages turn bodiless 4xx/5xx into problem+json.
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        // Routing explicitly here, so the timeout middleware below sees the endpoint's policy.
        app.UseRouting();

        // 3-5. Timeouts, identity, scopes. Per-key limiting is in the handlers, after the cache (RateLimiting).
        app.UseRequestTimeouts();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    public static WebApplication MapWebLensEndpoints(this WebApplication app)
    {
        app.MapWebLensHealth();
        app.MapWebLensOpenApi();

        var v1 = app.MapGroup("/v1")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        // One child group per capability: its scope policy and its request timeout.
        var search = v1.MapGroup("");
        search.RequireAuthorization(Scopes.Search);
        search.WithRequestTimeout(Scopes.Search);
        search.MapSearch();

        var fetch = v1.MapGroup("");
        fetch.RequireAuthorization(Scopes.Fetch);
        fetch.WithRequestTimeout(Scopes.Fetch);
        fetch.MapFetch();

        // The MCP tools: each needs its scope and draws on the same per-key limits as its /v1 endpoint.
        app.MapWebLensMcp();
        return app;
    }
}
