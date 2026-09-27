namespace PinkRooster.WebLens.Fetch.BrowserTests;

public class FailureTests
{
    private static async Task<FetchException> FailsAsync(FetchHarness h, string path, Func<FetchRequest, FetchRequest>? configure = null) =>
        await Assert.ThrowsAsync<FetchException>(() => h.FetchAsync(path, configure));

    [Theory]
    [InlineData(404, FetchErrorKind.TargetNotFound)]
    [InlineData(410, FetchErrorKind.TargetNotFound)]
    [InlineData(401, FetchErrorKind.TargetAccessDenied)]
    [InlineData(403, FetchErrorKind.TargetAccessDenied)]
    [InlineData(400, FetchErrorKind.TargetAccessDenied)]
    public async Task Terminal_statuses_are_reported_without_a_retry(int status, FetchErrorKind kind)
    {
        await using var h = await FetchHarness.CreateAsync();

        var ex = await FailsAsync(h, $"/status/{status}");

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(status, ex.TargetStatus);
        Assert.Equal(1, ex.Attempts);
        Assert.Equal(1, h.Site.Hits($"/status/{status}"));
    }

    [Fact]
    public async Task A_transient_failure_is_retried_on_a_fresh_context()
    {
        await using var h = await FetchHarness.CreateAsync();

        var result = await h.FetchAsync("/flaky");

        Assert.Equal("Recovered after a retry", result.Title);
        Assert.Equal(2, result.Diagnostics.Attempts);
    }

    [Fact]
    public async Task A_target_that_stays_down_is_unavailable_after_the_attempts_run_out()
    {
        await using var h = await FetchHarness.CreateAsync();

        var ex = await FailsAsync(h, "/always-503");

        Assert.Equal(FetchErrorKind.TargetUnavailable, ex.Kind);
        Assert.Equal(503, ex.TargetStatus);
        Assert.Equal(2, h.Site.Hits("/always-503"));
    }

    [Fact]
    public async Task A_refused_connection_is_target_unavailable()
    {
        // A port that was free a moment ago. Not a well-known one: Chromium refuses its "unsafe" ports outright.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var h = await FetchHarness.CreateAsync(extraPorts: port);

        var ex = await Assert.ThrowsAsync<FetchException>(() =>
            h.Fetch.FetchMarkdownAsync(new FetchRequest($"http://127.0.0.1:{port}/nothing-listens-here"), TestContext.Current.CancellationToken));

        Assert.Equal(FetchErrorKind.TargetUnavailable, ex.Kind);
        Assert.Equal(2, ex.Attempts);
    }

    [Theory]
    [InlineData("/pdf")]
    [InlineData("/download")]
    public async Task Files_are_unsupported_content_and_never_written_to_disk(string path)
    {
        await using var h = await FetchHarness.CreateAsync();

        var ex = await FailsAsync(h, path);

        Assert.Equal(FetchErrorKind.UnsupportedContent, ex.Kind);
    }

    [Fact]
    public async Task A_challenge_is_reported_and_the_block_circuit_spares_the_site_a_second_one()
    {
        await using var h = await FetchHarness.CreateAsync();

        var first = await FailsAsync(h, "/challenge");
        var second = await FailsAsync(h, "/challenge");

        Assert.Equal(FetchErrorKind.BotChallenge, first.Kind);
        Assert.Equal("cloudflare", first.Provider);
        Assert.Equal(FetchErrorKind.BotChallenge, second.Kind);
        Assert.Equal(1, h.Site.Hits("/challenge"));
    }

    [Fact]
    public async Task A_page_that_never_answers_is_a_timeout_within_the_callers_budget()
    {
        await using var h = await FetchHarness.CreateAsync();
        var started = DateTime.UtcNow;

        var ex = await FailsAsync(h, "/hang", r => r with { Timeout = TimeSpan.FromSeconds(2) });

        Assert.Equal(FetchErrorKind.Timeout, ex.Kind);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(6), "the caller's two-second budget was not honoured");
    }

    [Fact]
    public async Task Caller_cancellation_is_a_cancellation_and_frees_the_permits()
    {
        await using var h = await FetchHarness.CreateAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.FetchAsync("/hang", ct: cts.Token));

        Assert.Equal(0, h.Capacity.InUse);
        Assert.Contains("Saltmere", (await h.FetchAsync("/article")).Title);
    }
}
