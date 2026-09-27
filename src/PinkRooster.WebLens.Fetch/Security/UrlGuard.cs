using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Fetch;

/// <summary>Resolves host names for the URL guard. A seam so tests can make a name resolve anywhere.</summary>
internal interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

internal sealed class DnsHostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>Why a URL was refused. The reason is for logs; callers only ever hear "not allowed".</summary>
internal sealed record GuardDenial(string Reason);

/// <summary>
/// The L1 URL check, and the synchronous half L2 reuses for every request a page makes. Friendly fast refusals only:
/// between this check and the connection the name can re-resolve, so the egress proxy (L3), which resolves and connects
/// itself, is the boundary. Never rely on this alone.
/// </summary>
internal sealed class UrlGuard
{
    /// <summary>The denied address ranges, plus the IPv6 forms that embed an IPv4 address (as the proxy also refuses).</summary>
    private static readonly IPNetwork[] BuiltInDenied =
    [
        .. new[]
        {
            "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24",
            "192.168.0.0/16", "198.18.0.0/15", "224.0.0.0/4", "240.0.0.0/4",
            "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8", "64:ff9b::/96", "2002::/16", "2001::/32",
        }.Select(IPNetwork.Parse),
    ];

    private static readonly int[] DefaultPorts = [80, 443];

    private readonly SecurityOptions _options;
    private readonly IHostResolver _resolver;
    private readonly IPNetwork[] _configuredDenied;
    private readonly HashSet<int> _ports;

    public UrlGuard(IOptions<FetchOptions> options, IHostResolver resolver)
    {
        _options = options.Value.Security;
        _resolver = resolver;
        _configuredDenied = [.. _options.DeniedRanges.Select(IPNetwork.Parse)];
        _ports = new HashSet<int>(_options.AllowedPorts.Count > 0 ? _options.AllowedPorts : DefaultPorts);
    }

    /// <summary>
    /// Everything that can be decided without DNS: scheme, user info, port, a literal IP, a denied DNS suffix.
    /// Null when nothing here refuses the URL. Used by L1 before any browser work and by L2 for every page request.
    /// </summary>
    public GuardDenial? CheckWithoutDns(Uri url)
    {
        if (url.Scheme is not ("http" or "https" or "ws" or "wss"))
        {
            return new GuardDenial($"scheme {url.Scheme}");
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            return new GuardDenial("credentials in the URL");
        }

        if (!_ports.Contains(url.Port))
        {
            return new GuardDenial($"port {url.Port}");
        }

        var host = Host(url);
        if (host.Length == 0)
        {
            return new GuardDenial("no host");
        }

        if (_options.DeniedHostSuffixes.Any(suffix => host.Equals(suffix.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + suffix.TrimStart('.'), StringComparison.OrdinalIgnoreCase)))
        {
            return new GuardDenial("denied host suffix");
        }

        if (!_options.AllowPrivateNetworks && host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return new GuardDenial("localhost");
        }

        return Literal(url, host) is { } address ? CheckAddress(address) : null;
    }

    /// <summary>
    /// L1 in full: <see cref="CheckWithoutDns"/>, then the host's addresses, refused when <em>any</em> is in a denied
    /// range. Throws <see cref="FetchErrorKind.TargetNotAllowed"/>, or <see cref="FetchErrorKind.TargetUnavailable"/>
    /// for a name that does not resolve.
    /// </summary>
    public async Task CheckAsync(Uri url, CancellationToken ct)
    {
        if (CheckWithoutDns(url) is { } denial)
        {
            throw NotAllowed(denial);
        }

        var host = Host(url);
        if (Literal(url, host) is not null)
        {
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await _resolver.ResolveAsync(host, ct);
        }
        catch (SocketException ex)
        {
            throw new FetchException(FetchErrorKind.TargetUnavailable, "The target host name could not be resolved.", ex);
        }

        if (addresses.Length == 0)
        {
            throw new FetchException(FetchErrorKind.TargetUnavailable, "The target host name could not be resolved.");
        }

        foreach (var address in addresses)
        {
            if (CheckAddress(address) is { } denied)
            {
                throw NotAllowed(denied);
            }
        }
    }

    public GuardDenial? CheckAddress(IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (_configuredDenied.Any(range => range.Contains(normalized)))
        {
            return new GuardDenial("configured denied range");
        }

        if (!_options.AllowPrivateNetworks && BuiltInDenied.Any(range => range.Contains(normalized)))
        {
            return new GuardDenial("private or special-purpose address");
        }

        return null;
    }

    public static FetchException NotAllowed(GuardDenial denial) =>
        new(FetchErrorKind.TargetNotAllowed, "The target URL is not allowed.") { DenialReason = denial.Reason };

    /// <summary>The host as the network will see it: IDN-normalised (look-alike characters folded), without a trailing dot.</summary>
    private static string Host(Uri url) =>
        url.HostNameType == UriHostNameType.IPv6 ? url.Host.Trim('[', ']') : url.IdnHost.TrimEnd('.');

    /// <summary>The literal address in the URL, in whatever form it was written, or null for a name.</summary>
    private static IPAddress? Literal(Uri url, string host) =>
        url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || IPAddress.TryParse(host, out _)
            ? IPAddress.TryParse(host, out var address) ? address : null
            : null;
}
