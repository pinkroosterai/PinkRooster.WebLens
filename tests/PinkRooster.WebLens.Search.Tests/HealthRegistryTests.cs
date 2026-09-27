namespace PinkRooster.WebLens.Search.Tests;

public class HealthRegistryTests
{
    private const string A = "a";

    private static SearchHarness Harness() => SearchHarness.Create(1, new()
    {
        ["Routing:CircuitFailureThreshold"] = "3",
        ["Routing:BreakDuration"] = "00:00:30",
        ["Routing:RateLimitedDefaultCooldown"] = "00:00:10",
    });

    [Fact]
    public void Starts_unknown_and_eligible()
    {
        using var h = Harness();

        Assert.Equal(InstanceStateKind.Unknown, h.Health.Get(A).Kind);
        Assert.True(h.Health.IsEligible(A));
    }

    [Fact]
    public void Success_makes_it_healthy_and_a_failure_degrades_it()
    {
        using var h = Harness();

        h.Health.ReportSuccess(A);
        Assert.Equal(InstanceStateKind.Healthy, h.Health.Get(A).Kind);

        h.Health.ReportTransportFailure(A);
        Assert.Equal(InstanceStateKind.Degraded, h.Health.Get(A).Kind);
        Assert.True(h.Health.IsEligible(A));

        h.Health.ReportSuccess(A);
        Assert.Equal(InstanceStateKind.Healthy, h.Health.Get(A).Kind);
    }

    [Fact]
    public void Consecutive_failures_open_the_circuit_until_the_break_duration_passes()
    {
        using var h = Harness();

        for (var i = 0; i < 3; i++)
        {
            h.Health.ReportTransportFailure(A);
        }

        Assert.Equal(InstanceStateKind.OpenCircuit, h.Health.Get(A).Kind);
        Assert.False(h.Health.IsEligible(A));
        Assert.False(h.Health.TryBeginAttempt(A, out _));
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromSeconds(30), h.Health.EarliestRecovery());

        h.Time.Advance(TimeSpan.FromSeconds(30));

        Assert.True(h.Health.IsEligible(A));
    }

    [Fact]
    public void Half_open_allows_exactly_one_probe()
    {
        using var h = Harness();
        for (var i = 0; i < 3; i++)
        {
            h.Health.ReportTransportFailure(A);
        }

        h.Time.Advance(TimeSpan.FromSeconds(30));

        Assert.True(h.Health.TryBeginAttempt(A, out _));
        Assert.Equal(InstanceStateKind.HalfOpen, h.Health.Get(A).Kind);
        Assert.False(h.Health.IsEligible(A));
        Assert.False(h.Health.TryBeginAttempt(A, out _));
    }

    [Fact]
    public void A_successful_probe_closes_the_circuit()
    {
        using var h = Harness();
        for (var i = 0; i < 3; i++)
        {
            h.Health.ReportTransportFailure(A);
        }

        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Health.TryBeginAttempt(A, out _);
        h.Health.ReportSuccess(A);

        Assert.Equal(InstanceStateKind.Healthy, h.Health.Get(A).Kind);
    }

    [Fact]
    public void A_failed_probe_reopens_the_circuit_for_longer()
    {
        using var h = Harness();
        for (var i = 0; i < 3; i++)
        {
            h.Health.ReportTransportFailure(A);
        }

        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Health.TryBeginAttempt(A, out _);
        h.Health.ReportTransportFailure(A);

        var state = h.Health.Get(A);
        Assert.Equal(InstanceStateKind.OpenCircuit, state.Kind);
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromSeconds(60), state.Until);
    }

    [Fact]
    public void Releasing_a_probe_frees_the_permit_without_changing_health()
    {
        using var h = Harness();
        for (var i = 0; i < 3; i++)
        {
            h.Health.ReportTransportFailure(A);
        }

        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Health.TryBeginAttempt(A, out _);
        h.Health.ReleaseProbe(A);

        Assert.Equal(InstanceStateKind.HalfOpen, h.Health.Get(A).Kind);
        Assert.True(h.Health.TryBeginAttempt(A, out _));
    }

    [Fact]
    public void Rate_limiting_uses_retry_after_or_the_default_cooldown_and_expires_on_its_own()
    {
        using var h = Harness();

        h.Health.ReportRateLimited(A, TimeSpan.FromSeconds(45));
        Assert.Equal(InstanceStateKind.RateLimited, h.Health.Get(A).Kind);
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromSeconds(45), h.Health.Get(A).Until);
        Assert.False(h.Health.IsEligible(A));

        h.Time.Advance(TimeSpan.FromSeconds(45));
        Assert.True(h.Health.IsEligible(A));

        h.Health.TryBeginAttempt(A, out _);
        h.Health.ReportRateLimited(A, null);
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromSeconds(10), h.Health.Get(A).Until);
    }

    [Fact]
    public void An_absurd_retry_after_is_capped()
    {
        using var h = Harness();

        h.Health.ReportRateLimited(A, TimeSpan.FromDays(30));

        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromHours(1), h.Health.Get(A).Until);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Capability_states_last_for_the_capability_ttl_then_expire_without_a_restart(bool jsonUnsupported)
    {
        using var h = Harness();

        if (jsonUnsupported)
        {
            h.Health.ReportJsonUnsupported(A);
        }
        else
        {
            h.Health.ReportProtocolIncompatible(A);
        }

        Assert.False(h.Health.IsEligible(A));
        h.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(h.Health.IsEligible(A));
        Assert.True(h.Health.TryBeginAttempt(A, out var isProbe));
        Assert.True(isProbe);
    }

    [Fact]
    public void Healthy_instances_need_no_probe()
    {
        using var h = Harness();

        Assert.True(h.Health.TryBeginAttempt(A, out var isProbe));
        Assert.False(isProbe);
    }

    [Fact]
    public void An_expired_cooldown_lets_one_probe_through_rather_than_every_waiting_request()
    {
        using var h = Harness();
        h.Health.ReportRateLimited(A, TimeSpan.FromSeconds(10));
        h.Time.Advance(TimeSpan.FromSeconds(10));

        Assert.True(h.Health.TryBeginAttempt(A, out var isProbe));
        Assert.True(isProbe);
        Assert.False(h.Health.TryBeginAttempt(A, out _));

        h.Health.ReportSuccess(A);
        Assert.Equal(InstanceStateKind.Healthy, h.Health.Get(A).Kind);
    }

    [Fact]
    public void A_late_failure_from_an_attempt_already_in_flight_does_not_close_an_open_circuit()
    {
        using var h = Harness();
        for (var i = 0; i < 3; i++)
        {
            h.Health.ReportTransportFailure(A);
        }

        var opened = h.Health.Get(A);

        h.Health.ReportTransportFailure(A);
        h.Health.ReportSuccess(A);

        Assert.Same(opened, h.Health.Get(A));
    }

    [Fact]
    public void A_late_failure_does_not_cut_a_cooldown_short()
    {
        using var h = Harness();
        h.Health.ReportRateLimited(A, TimeSpan.FromSeconds(45));

        h.Health.ReportTransportFailure(A);

        Assert.Equal(InstanceStateKind.RateLimited, h.Health.Get(A).Kind);
        Assert.False(h.Health.IsEligible(A));
    }

    [Fact]
    public void A_cooldown_keeps_the_break_count_so_the_next_open_circuit_still_lasts_longer()
    {
        using var h = Harness();
        for (var i = 0; i < 3; i++)
        {
            h.Health.ReportTransportFailure(A);
        }

        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Health.TryBeginAttempt(A, out _);
        h.Health.ReportRateLimited(A, TimeSpan.FromSeconds(10));
        h.Time.Advance(TimeSpan.FromSeconds(10));
        h.Health.TryBeginAttempt(A, out _);
        h.Health.ReportTransportFailure(A);

        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromSeconds(60), h.Health.Get(A).Until);
    }

    [Fact]
    public void A_probe_permit_that_is_never_returned_lapses_after_the_total_budget()
    {
        using var h = Harness();
        for (var i = 0; i < 3; i++)
        {
            h.Health.ReportTransportFailure(A);
        }

        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Health.TryBeginAttempt(A, out _);
        Assert.False(h.Health.IsEligible(A));

        h.Time.Advance(TimeSpan.FromSeconds(8));

        Assert.True(h.Health.IsEligible(A));
        Assert.True(h.Health.TryBeginAttempt(A, out var isProbe));
        Assert.True(isProbe);
    }
}
