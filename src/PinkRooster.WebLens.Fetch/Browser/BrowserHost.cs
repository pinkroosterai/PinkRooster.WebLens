using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace PinkRooster.WebLens.Fetch;

/// <summary>
/// One launched browser. Fetches take a lease on it; a generation that died or is being recycled accepts no new leases
/// and is closed once its last lease is returned.
/// </summary>
internal sealed class BrowserGeneration(int number, IBrowser browser)
{
    private readonly Lock _gate = new();
    private int _leases;
    private int _contexts;
    private bool _retiring;
    private volatile bool _alive = true;

    public int Number { get; } = number;
    public IBrowser Browser { get; } = browser;
    public bool IsAlive => _alive;
    public int ContextsCreated => Volatile.Read(ref _contexts);

    public void MarkDead() => _alive = false;

    public bool TryAddLease()
    {
        lock (_gate)
        {
            if (_retiring || !_alive)
            {
                return false;
            }

            _leases++;
            _contexts++;
            return true;
        }
    }

    /// <summary>Returns true when this was the last lease of a retiring generation: the caller closes the browser.</summary>
    public bool ReleaseLease()
    {
        lock (_gate)
        {
            _leases--;
            return _retiring && _leases == 0;
        }
    }

    /// <summary>Stops new leases. Returns true when no lease is out, so the caller closes the browser now.</summary>
    public bool Retire()
    {
        lock (_gate)
        {
            _retiring = true;
            return _leases == 0;
        }
    }
}

/// <summary>A fetch's hold on one browser generation. Returning it may close a retired browser.</summary>
internal sealed class BrowserLease(BrowserHost host, BrowserGeneration generation) : IDisposable
{
    private int _released;

    public BrowserGeneration Generation { get; } = generation;
    public IBrowser Browser => Generation.Browser;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            host.Release(Generation);
        }
    }
}

/// <summary>
/// Owns Playwright and the current browser generation. A disconnected or failed browser is replaced on the
/// next acquisition under a lock; nothing is moved between browsers mid-request. A browser due for recycling keeps serving
/// while its replacement launches in the background, so no fetch waits for a launch.
/// </summary>
internal sealed partial class BrowserHost(IOptions<FetchOptions> options, TimeProvider time, ILogger<BrowserHost> logger) : IAsyncDisposable, IDisposable
{
    /// <summary>After a failed launch, fetches fail fast for this long instead of each trying to launch again.</summary>
    private static readonly TimeSpan LaunchFailureBackoff = TimeSpan.FromSeconds(5);

    // Read on use, not in the constructor: the host builds this while resolving hosted services, before start-up
    // validation runs, and an invalid configuration must fail there with its own message.
    private BrowserOptions Settings => options.Value.Browser;
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private IPlaywright? _playwright;
    private BrowserGeneration? _current;
    private DateTimeOffset? _launchFailedAt;
    private int _recycling;
    private volatile bool _disposed;

    /// <summary>The generation new fetches would use, or null before the first launch. For health and tests.</summary>
    public BrowserGeneration? Current => Volatile.Read(ref _current);

    public async Task<BrowserLease> AcquireAsync(CancellationToken ct)
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var generation = Current;
            if (generation is not null && Settings.RecycleAfterContexts > 0 && generation.ContextsCreated >= Settings.RecycleAfterContexts)
            {
                RecycleInBackground(generation);
            }

            if (generation is not null && generation.TryAddLease())
            {
                return new BrowserLease(this, generation);
            }

            // None yet, or it died: nothing to serve from until a browser is up.
            await ReplaceAsync(generation, ct);
        }
    }

    /// <summary>Launches the replacement of a generation due for recycling, once, while it keeps serving.</summary>
    private void RecycleInBackground(BrowserGeneration due)
    {
        if (Interlocked.CompareExchange(ref _recycling, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Not a request's work, so no request token: disposal is what stops it.
                await ReplaceAsync(due, CancellationToken.None);
            }
            catch (Exception ex) when (ex is FetchException or ObjectDisposedException)
            {
                // A failed launch is logged by ReplaceAsync, and the due generation keeps serving; the next acquisition tries
                // again after the launch back-off. Disposal mid-launch needs nothing more.
            }
            finally
            {
                Volatile.Write(ref _recycling, 0);
            }
        });
    }

    internal void Release(BrowserGeneration generation)
    {
        if (generation.ReleaseLease())
        {
            CloseInBackground(generation);
        }
    }

    private async Task ReplaceAsync(BrowserGeneration? stale, CancellationToken ct)
    {
        await _launchLock.WaitAsync(ct);
        try
        {
            if (!ReferenceEquals(Current, stale))
            {
                return; // someone else replaced it while we waited
            }

            if (_launchFailedAt is { } failedAt && time.GetUtcNow() - failedAt < LaunchFailureBackoff)
            {
                throw Unavailable(null);
            }

            var number = (stale?.Number ?? 0) + 1;
            BrowserGeneration next;
            try
            {
                next = await LaunchAsync(number);
            }
            // Any launch failure: Playwright's own, a timeout, or the driver process not starting at all (a missing or
            // unexecutable binary is a Win32Exception). Each is "the browser is unavailable" to callers, never a 500.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _launchFailedAt = time.GetUtcNow();
                LogLaunchFailed(ex);
                throw Unavailable(ex);
            }

            _launchFailedAt = null;
            if (_disposed)
            {
                // Disposed while this launched in the background: nothing will ever close it otherwise.
                next.MarkDead();
                await next.Browser.CloseAsync();
                throw new ObjectDisposedException(nameof(BrowserHost));
            }

            Volatile.Write(ref _current, next);

            if (stale is not null)
            {
                LogReplaced(stale.Number, number, stale.IsAlive ? "recycled" : "disconnected");
                if (stale.Retire())
                {
                    CloseInBackground(stale);
                }
            }
            else
            {
                LogLaunched(number, next.Browser.Version);
            }
        }
        finally
        {
            _launchLock.Release();
        }
    }

    private async Task<BrowserGeneration> LaunchAsync(int number)
    {
        _playwright ??= await Playwright.CreateAsync();
        var browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Settings.Headless,
            ChromiumSandbox = Settings.ChromiumSandbox,
            // Empty (as an environment variable sets "none") selects the headless shell, like null.
            Channel = string.IsNullOrEmpty(Settings.Channel) ? null : Settings.Channel,
            Timeout = (float)Settings.LaunchTimeout.TotalMilliseconds,
            Env = LaunchEnvironment(),
        });

        var generation = new BrowserGeneration(number, browser);
        browser.Disconnected += (_, _) =>
        {
            if (generation.IsAlive)
            {
                generation.MarkDead();
                LogDisconnected(generation.Number);
            }
        };
        return generation;
    }

    /// <summary>
    /// The variables the browser needs, copied by name, and nothing else (L4): API keys, connection strings
    /// and proxy credentials held by this process must not be inherited by a process that runs untrusted page script.
    /// </summary>
    internal static Dictionary<string, string> LaunchEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in LaunchEnvironmentNames)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    /// <summary>Paths, locale, time zone and fonts on Linux; the profile and system folders Chromium needs on Windows.</summary>
    internal static readonly string[] LaunchEnvironmentNames =
    [
        "PATH", "HOME", "TMPDIR", "LANG", "LC_ALL", "TZ", "FONTCONFIG_PATH", "FONTCONFIG_FILE", "XDG_RUNTIME_DIR", "PLAYWRIGHT_BROWSERS_PATH",
        "SystemRoot", "SystemDrive", "windir", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "ComSpec",
    ];

    private void CloseInBackground(BrowserGeneration generation) =>
        _ = Task.Run(async () =>
        {
            try
            {
                generation.MarkDead();
                await generation.Browser.CloseAsync();
            }
            catch (Exception ex)
            {
                // Nothing awaits this; a browser that will not close cleanly is logged and left to the process.
                LogCloseFailed(generation.Number, ex);
            }
        });

    private static FetchException Unavailable(Exception? inner) =>
        new(FetchErrorKind.BrowserUnavailable, "The browser is not available.", inner) { RetryAfter = LaunchFailureBackoff };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Current is { } generation)
        {
            generation.MarkDead();
            try
            {
                await generation.Browser.CloseAsync();
            }
            catch (PlaywrightException ex)
            {
                LogCloseFailed(generation.Number, ex);
            }
        }

        _playwright?.Dispose();
        _launchLock.Dispose();
    }

    /// <summary>For containers disposed synchronously (tests, tools). The host disposes asynchronously.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    [LoggerMessage(Level = LogLevel.Information, Message = "Browser generation {Generation} launched (version {Version})")]
    private partial void LogLaunched(int generation, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Browser generation {Old} {Reason}; generation {New} launched")]
    private partial void LogReplaced(int old, int @new, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Browser generation {Generation} disconnected")]
    private partial void LogDisconnected(int generation);

    [LoggerMessage(Level = LogLevel.Error, Message = "Browser launch failed")]
    private partial void LogLaunchFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Browser generation {Generation} did not close cleanly")]
    private partial void LogCloseFailed(int generation, Exception exception);
}
