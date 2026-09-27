using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Api.Hosting;

/// <summary>Whether Valkey is TCP-reachable, probed in the background. Reported in details; never affects readiness.</summary>
internal sealed partial class ValkeyProbe(IOptions<ApiOptions> options, ILogger<ValkeyProbe> logger) : BackgroundService, IHealthCheck
{
    public const string Name = "cache";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private volatile bool _reachable;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ValkeyConnectionString))
        {
            return Task.FromResult(HealthCheckResult.Healthy("L1 only; no Valkey configured."));
        }

        // Degraded, not unhealthy: requests still succeed from L1 and fresh executions.
        return Task.FromResult(_reachable
            ? HealthCheckResult.Healthy("Valkey is reachable.")
            : HealthCheckResult.Degraded("Valkey is not reachable; serving from L1 and fresh executions."));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ValkeyConnectionString))
        {
            return;
        }

        var endpoints = WebLensServices.ValkeyConfiguration(options.Value.ValkeyConnectionString).EndPoints;
        using var timer = new PeriodicTimer(Interval);
        do
        {
            var reachable = false;
            foreach (var endpoint in endpoints)
            {
                reachable |= await ReachableAsync(endpoint, stoppingToken);
            }

            if (_reachable && !reachable)
            {
                LogUnreachable();
            }

            _reachable = reachable;
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static async Task<bool> ReachableAsync(EndPoint endpoint, CancellationToken ct)
    {
        var (host, port) = endpoint switch
        {
            DnsEndPoint dns => (dns.Host, dns.Port),
            IPEndPoint ip => (ip.Address.ToString(), ip.Port),
            _ => ("", 0),
        };
        try
        {
            using var client = new TcpClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(host, port, deadline.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Valkey is not reachable; the cache serves from L1 until it returns")]
    private partial void LogUnreachable();
}

/// <summary>
/// Health endpoints. <c>live</c>: the process answers. <c>ready</c>: start-up is complete and at least one capability can
/// work (either, not both: a SearXNG outage must not take a working fetch out of rotation); a status word only.
/// <c>details</c>: every check with its data, for a valid key.
/// </summary>
internal static class Health
{
    public static IServiceCollection AddWebLensHealth(this IServiceCollection services)
    {
        services.AddSingleton<ValkeyProbe>();
        services.AddHostedService(sp => sp.GetRequiredService<ValkeyProbe>());
        services.AddHealthChecks().AddCheck<ValkeyProbe>(ValkeyProbe.Name);
        return services;
    }

    public static WebApplication MapWebLensHealth(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Text("Healthy")).AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/health/ready", ReadyAsync).AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/health/details", DetailsAsync).RequireAuthorization().ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> ReadyAsync(HealthCheckService health, IHostApplicationLifetime lifetime, CancellationToken ct)
    {
        if (!lifetime.ApplicationStarted.IsCancellationRequested || lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return Results.Text("Unhealthy", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var report = await health.CheckHealthAsync(check => check.Tags.Contains("capability"), ct);
        var usable = report.Entries.Values.Any(e => e.Status != HealthStatus.Unhealthy);
        return usable ? Results.Text("Healthy") : Results.Text("Unhealthy", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task DetailsAsync(HttpContext http, HealthCheckService health, CancellationToken ct)
    {
        var report = await health.CheckHealthAsync(ct);
        http.Response.ContentType = "application/json";
        await using var json = new Utf8JsonWriter(http.Response.Body, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("status", report.Status.ToString());
        json.WriteStartObject("checks");
        foreach (var (name, entry) in report.Entries)
        {
            json.WriteStartObject(name);
            json.WriteString("status", entry.Status.ToString());
            json.WriteString("description", entry.Description);
            json.WriteStartObject("data");
            foreach (var (key, value) in entry.Data)
            {
                json.WritePropertyName(key);
                switch (value)
                {
                    case bool b: json.WriteBooleanValue(b); break;
                    case int i: json.WriteNumberValue(i); break;
                    default: json.WriteStringValue(value.ToString()); break;
                }
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }
}
