using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PinkRooster.WebLens.Fetch.BrowserTests;

/// <summary>
/// The real egress proxy (deploy/egress-proxy) in a container on the host network, for the tests that must prove the
/// boundary itself (L3). Needs Docker. Without it these tests skip, unless WEBLENS_REQUIRE_EGRESS_PROXY=1
/// (set in CI), which turns a missing Docker into a failure so the security suite can never silently not run.
/// </summary>
internal sealed class EgressProxy : IAsyncDisposable
{
    private const string Image = "weblens-egress-proxy:test";
    private readonly string _container;

    private EgressProxy(string container, int port)
    {
        _container = container;
        Port = port;
    }

    public int Port { get; }
    public string Server => $"http://127.0.0.1:{Port}";

    /// <summary>Skips the calling test when Docker is missing, or fails it when the environment demands the proxy.</summary>
    public static void RequireDocker()
    {
        var available = Run("docker", "info --format {{.ServerVersion}}", TimeSpan.FromSeconds(20)).ExitCode == 0;
        if (!available && Environment.GetEnvironmentVariable("WEBLENS_REQUIRE_EGRESS_PROXY") == "1")
        {
            Assert.Fail("WEBLENS_REQUIRE_EGRESS_PROXY=1 but Docker is not available: the egress-proxy tests cannot run.");
        }

        Assert.SkipUnless(available, "Docker is not available, so the real egress proxy cannot run (set WEBLENS_REQUIRE_EGRESS_PROXY=1 to make this a failure).");
    }

    /// <param name="allow">The one address the proxy lets through: the fixture site. Nothing a deployment would ever do.</param>
    public static async Task<EgressProxy> StartAsync(IPEndPoint allow)
    {
        RequireDocker();
        var build = Run("docker", $"build -q -t {Image} {RepoPath("deploy", "egress-proxy")}", TimeSpan.FromMinutes(5));
        Assert.True(build.ExitCode == 0, $"docker build failed: {build.Output}");

        var port = FreePort();
        var name = $"weblens-egress-test-{Guid.NewGuid():N}"[..40];
        var run = Run("docker",
            $"run -d --rm --name {name} --network host {Image} --listen-ip 127.0.0.1 --listen-port {port} --allow-address {allow.Address}:{allow.Port}",
            TimeSpan.FromMinutes(1));
        Assert.True(run.ExitCode == 0, $"docker run failed: {run.Output}");

        var proxy = new EgressProxy(name, port);
        await proxy.WaitUntilListeningAsync();
        return proxy;
    }

    public string Logs() => Run("docker", $"logs {_container}", TimeSpan.FromSeconds(20)).Output;

    public ValueTask DisposeAsync()
    {
        Run("docker", $"rm -f {_container}", TimeSpan.FromSeconds(30));
        return ValueTask.CompletedTask;
    }

    private async Task WaitUntilListeningAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, Port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        Assert.Fail($"The egress proxy did not start listening: {Logs()}");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string RepoPath(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(string.Join('/', parts));
    }

    /// <summary>The first address on Docker's default bridge network: where SearXNG-like neighbours live.</summary>
    public static IPAddress BridgeNeighbour()
    {
        var subnet = Run("docker", "network inspect bridge --format \"{{(index .IPAM.Config 0).Subnet}}\"", TimeSpan.FromSeconds(20)).Output.Trim();
        var network = IPNetwork.Parse(subnet);
        var bytes = network.BaseAddress.GetAddressBytes();
        bytes[^1] = 2;
        return new IPAddress(bytes);
    }

    private static (int ExitCode, string Output) Run(string file, string arguments, TimeSpan timeout)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
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
