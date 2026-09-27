using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PinkRooster.WebLens.Api.Tests;

/// <summary>
/// A real Valkey in Docker for the L2 cache tests, not a fake. Without Docker the tests skip, unless
/// WEBLENS_REQUIRE_VALKEY=1 (set in CI), which makes a missing Docker a failure.
/// </summary>
internal sealed class ValkeyContainer : IAsyncDisposable
{
    private const string Image = "valkey/valkey:9-alpine";
    private readonly string _name;

    private ValkeyContainer(string name, int port)
    {
        _name = name;
        Port = port;
    }

    public int Port { get; }
    public string ConnectionString => $"127.0.0.1:{Port}";

    public static async Task<ValkeyContainer> StartAsync()
    {
        var available = Docker("info --format \"{{.ServerVersion}}\"").ExitCode == 0;
        if (!available && Environment.GetEnvironmentVariable("WEBLENS_REQUIRE_VALKEY") == "1")
        {
            Assert.Fail("WEBLENS_REQUIRE_VALKEY=1 but Docker is not available: the Valkey tests cannot run.");
        }

        Assert.SkipUnless(available, "Docker is not available, so Valkey cannot run (set WEBLENS_REQUIRE_VALKEY=1 to make this a failure).");

        var port = FreePort();
        var name = $"weblens-valkey-test-{Guid.NewGuid():N}"[..40];
        var run = Docker($"run -d --rm --name {name} -p 127.0.0.1:{port}:6379 {Image}");
        Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");
        var container = new ValkeyContainer(name, port);
        await container.WaitUntilReadyAsync();
        return container;
    }

    /// <summary>Stops the server mid-run; the container's port mapping goes with it, so connections are refused.</summary>
    public void Stop() => Docker($"stop -t 0 {_name}");

    public ValueTask DisposeAsync()
    {
        Docker($"rm -f {_name}");
        return ValueTask.CompletedTask;
    }

    private async Task WaitUntilReadyAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            if (Docker($"exec {_name} valkey-cli ping").Output.Contains("PONG", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Valkey did not become ready.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static (int ExitCode, string Output) Docker(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                process.Kill(entireProcessTree: true);
                return (-1, "timed out");
            }

            return (process.ExitCode, output.Result + error.Result);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
