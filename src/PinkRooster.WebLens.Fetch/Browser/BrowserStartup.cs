using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Fetch;

/// <summary>Launches the browser in the background when the host starts, without holding up start-up.</summary>
internal sealed partial class BrowserStartup(
    BrowserHost host,
    IOptions<FetchOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<BrowserStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Browser.LaunchOnStart)
        {
            _ = Task.Run(WarmUpAsync, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WarmUpAsync()
    {
        // The host's own stopping token: nothing here to dispose, whatever order the host stops and disposes in.
        var stopping = lifetime.ApplicationStopping;
        try
        {
            using var lease = await host.AcquireAsync(stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            LogStoppedBeforeLaunch();
        }
        catch (Exception ex)
        {
            // The first fetch tries again; until then the fetch capability is simply not up.
            LogWarmUpFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The browser could not be launched at start-up; the first fetch will try again")]
    private partial void LogWarmUpFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The host stopped before the browser was launched")]
    private partial void LogStoppedBeforeLaunch();
}
