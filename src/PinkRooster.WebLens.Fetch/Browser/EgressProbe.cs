using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Fetch;

/// <summary>
/// Whether the egress proxy is TCP-reachable, probed on an interval in the background so a health request
/// never makes an outbound call. Also notes how long every browser context has been busy, for the "degraded" state.
/// </summary>
internal sealed partial class EgressProbe(IOptions<FetchOptions> options, CapacityGate capacity, TimeProvider time, ILogger<EgressProbe> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private volatile bool _proxyReachable;
    private long _saturatedSinceTicks;

    /// <summary>Null when no proxy is configured (Development, Testing).</summary>
    public bool? ProxyReachable => string.IsNullOrWhiteSpace(options.Value.Security.EgressProxy.Server) ? null : _proxyReachable;

    /// <summary>How long every context has been in use, or zero.</summary>
    public TimeSpan SaturatedFor
    {
        get
        {
            var since = Interlocked.Read(ref _saturatedSinceTicks);
            return since == 0 ? TimeSpan.Zero : time.GetUtcNow() - new DateTimeOffset(since, TimeSpan.Zero);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            await ProbeAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ProbeAsync(CancellationToken ct)
    {
        var full = capacity.InUse >= options.Value.Browser.MaxConcurrentContexts;
        if (!full)
        {
            Interlocked.Exchange(ref _saturatedSinceTicks, 0);
        }
        else
        {
            Interlocked.CompareExchange(ref _saturatedSinceTicks, time.GetUtcNow().UtcTicks, 0);
        }

        if (!Uri.TryCreate(options.Value.Security.EgressProxy.Server, UriKind.Absolute, out var proxy))
        {
            return;
        }

        var reachable = false;
        try
        {
            using var client = new TcpClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(proxy.Host, proxy.Port, deadline.Token);
            reachable = true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            if (_proxyReachable)
            {
                LogUnreachable(proxy.Host, proxy.Port, ex.GetType().Name);
            }
        }

        _proxyReachable = reachable;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The egress proxy at {Host}:{Port} is not reachable ({Reason}); fetch is not ready")]
    private partial void LogUnreachable(string host, int port, string reason);
}
