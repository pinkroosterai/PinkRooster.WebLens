using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Search;

internal sealed record CapabilitySnapshot(
    bool IsSearxng,
    IReadOnlySet<string> Engines,
    IReadOnlySet<string> Categories,
    DateTimeOffset FetchedAt);

/// <summary>
/// Per-instance <c>/config</c> snapshot with a TTL. Never a prerequisite for a search: it is fetched in the background,
/// except on the failure path where a 403 needs it to be classified.
/// </summary>
internal sealed partial class CapabilityCache(
    IHttpClientFactory httpClients,
    IOptions<SearchOptions> options,
    TimeProvider time,
    ILogger<CapabilityCache> logger)
{
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public CapabilitySnapshot? Snapshot;
        public DateTimeOffset? FailedAt;
        public int Refreshing;
    }

    /// <summary>The snapshot when it is still within its TTL; otherwise null.</summary>
    public CapabilitySnapshot? GetFresh(string instance) =>
        _entries.TryGetValue(instance, out var entry) && Volatile.Read(ref entry.Snapshot) is { } snapshot
        && time.GetUtcNow() - snapshot.FetchedAt < options.Value.Capabilities.Ttl
            ? snapshot
            : null;

    /// <summary>Starts a refresh when there is no fresh snapshot and none is running. Returns immediately.</summary>
    public void RefreshInBackground(SearchInstance instance)
    {
        if (GetFresh(instance.Name) is not null)
        {
            return;
        }

        var entry = _entries.GetOrAdd(instance.Name, _ => new Entry());
        if (entry.FailedAt is { } failedAt && time.GetUtcNow() - failedAt < FailureBackoff)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref entry.Refreshing, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Detached from any request: nothing but the probe's own deadline should end it.
                await ProbeAsync(instance, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Nothing awaits this task, so an exception that escaped here would vanish unlogged.
                Failed(entry, instance.Name, ex.GetType().Name);
            }
            finally
            {
                Volatile.Write(ref entry.Refreshing, 0);
            }
        });
    }

    /// <summary>
    /// Fetches <c>/config</c> now, within one attempt's time: on the failure path a <c>/config</c> that hangs must not spend
    /// the rest of the search's budget. Running out of that time is a failed read (null); <paramref name="ct"/> cancelling throws.
    /// </summary>
    public async Task<CapabilitySnapshot?> ProbeAsync(SearchInstance instance, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(instance.Name, _ => new Entry());
        using var deadline = new CancellationTokenSource(options.Value.Timeouts.Attempt, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            return await FetchAsync(instance, entry, linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Failed(entry, instance.Name, "timed out");
            return null;
        }
    }

    private async Task<CapabilitySnapshot?> FetchAsync(SearchInstance instance, Entry entry, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, instance.ConfigUri);
            request.Headers.Accept.ParseAdd("application/json");
            foreach (var (name, value) in instance.Headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            var client = httpClients.CreateClient(SearchServiceCollectionExtensions.HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                Failed(entry, instance.Name, $"status {(int)response.StatusCode}");
                return null;
            }

            await using var stream = await LimitedStream.OpenAsync(response, options.Value.Transport.MaxResponseBytes, ct);
            var config = await JsonSerializer.DeserializeAsync(stream, SearxJsonContext.Default.SearxConfig, ct);
            var snapshot = ToSnapshot(config);
            if (snapshot is null)
            {
                Failed(entry, instance.Name, "not a SearXNG /config document");
                return null;
            }

            Volatile.Write(ref entry.Snapshot, snapshot);
            entry.FailedAt = null;
            return snapshot;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException)
        {
            Failed(entry, instance.Name, ex.GetType().Name);
            return null;
        }
    }

    private CapabilitySnapshot? ToSnapshot(SearxConfig? config)
    {
        // "Is SearXNG" is judged by shape: a reverse proxy or WAF answering /config would not produce a version plus an engine list.
        if (config is null || string.IsNullOrEmpty(config.Version) || config.Engines is null)
        {
            return null;
        }

        var engines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var engine in config.Engines.Where(e => e.Enabled == true))
        {
            if (!string.IsNullOrWhiteSpace(engine.Name))
            {
                engines.Add(NormalizeEngineName(engine.Name));
            }

            if (!string.IsNullOrWhiteSpace(engine.Shortcut))
            {
                engines.Add(engine.Shortcut);
            }
        }

        var categories = new HashSet<string>(config.Categories ?? [], StringComparer.OrdinalIgnoreCase);
        return new CapabilitySnapshot(true, engines, categories, time.GetUtcNow());
    }

    /// <summary>Engines with a space in their name are written with an underscore in a bang (<c>!google_cse</c>).</summary>
    public static string NormalizeEngineName(string name) => name.Trim().Replace(' ', '_');

    private void Failed(Entry entry, string instance, string reason)
    {
        entry.FailedAt = time.GetUtcNow();
        LogConfigFailed(instance, reason);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not read /config from search instance {Instance}: {Reason}")]
    private partial void LogConfigFailed(string instance, string reason);
}
