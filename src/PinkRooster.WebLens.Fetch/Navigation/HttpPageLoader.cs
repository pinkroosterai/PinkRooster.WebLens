using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Fetch;

/// <summary>
/// The HTTP-first attempt's transport: one plain GET through the egress proxy, with the
/// browser profile's language, following redirects itself so L1 checks every hop. Returns the response as
/// a <see cref="RenderedPage"/>, so the browser path's classification and block detection apply unchanged, or null when
/// the attempt could not get an answer (timeout, transport error): the fetch then renders in the browser.
/// </summary>
internal sealed partial class HttpPageLoader : IDisposable
{
    /// <summary>Chromium's own limit.</summary>
    private const int MaxRedirects = 20;

    /// <summary>
    /// An honest user agent unless the operator set one (Browser:UserAgent): a plain client claiming to be Chrome would be the
    /// incoherent, disguised profile WebLens does not build.
    /// </summary>
    public const string DefaultUserAgent = "Mozilla/5.0 (compatible; WebLens/1.0)";

    private static readonly Regex MetaCharset = new("""<meta[^>]+charset\s*=\s*["']?([A-Za-z0-9_\-]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly FetchOptions _options;
    private readonly UrlGuard _guard;
    private readonly TimeProvider _time;
    private readonly ILogger<HttpPageLoader> _logger;
    private readonly HttpClient _client;

    static HttpPageLoader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public HttpPageLoader(IOptions<FetchOptions> options, UrlGuard guard, TimeProvider time, ILogger<HttpPageLoader> logger)
    {
        _options = options.Value;
        _guard = guard;
        _time = time;
        _logger = logger;

        var proxy = _options.Security.EgressProxy;
        var handler = new SocketsHttpHandler
        {
            // The egress proxy when configured, and never a proxy from the environment: the start-up rules decide this.
            UseProxy = !string.IsNullOrWhiteSpace(proxy.Server),
            Proxy = string.IsNullOrWhiteSpace(proxy.Server)
                ? null
                : new WebProxy(proxy.Server) { Credentials = proxy.Username is null ? null : new NetworkCredential(proxy.Username, proxy.Password) },
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        _client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public async Task<RenderedPage?> LoadAsync(NormalizedFetch fetch, TimeSpan timeout, CancellationToken ct)
    {
        var started = _time.GetTimestamp();
        using var limit = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        var url = fetch.Url;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var request = Request(url);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects || !Uri.TryCreate(url, location, out var next) || next.Scheme is not ("http" or "https"))
                    {
                        LogFellBack(fetch.Url.Host, "an unusable redirect");
                        return null;
                    }

                    // L1 on every hop, with DNS: a denied hop refuses the fetch, as on the browser path.
                    await _guard.CheckAsync(next, linked.Token);
                    url = next;
                    continue;
                }

                return await PageAsync(url, response, started, linked.Token);
            }
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            LogFellBack(fetch.Url.Host, "the time limit");
            return null;
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ProxyTunnelError && ex.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
        {
            // An https refusal by the egress proxy arrives as a failed CONNECT.
            // Shaped like the plain-http refusal, so the classifier treats both the same way.
            return Denied(url, started);
        }
        catch (HttpRequestException ex)
        {
            LogTransportFailed(fetch.Url.Host, ex.HttpRequestError);
            return null;
        }
    }

    private HttpRequestMessage Request(Uri url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.Browser.UserAgent ?? DefaultUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", _options.Browser.Locale);
        return request;
    }

    private async Task<RenderedPage> PageAsync(Uri url, HttpResponseMessage response, long started, CancellationToken ct)
    {
        var headers = Headers(response);
        headers.TryGetValue("content-type", out var contentType);
        var status = (int)response.StatusCode;
        var html = "";
        var bytes = 0;
        if (RenderedPage.IsHtmlType(contentType))
        {
            var max = _options.Limits.MaxRenderedHtmlBytes;
            var body = await ReadAsync(response.Content, max + 1, ct);
            if (body.Length > max)
            {
                throw new FetchException(FetchErrorKind.UnsupportedContent, "The rendered page is larger than this service accepts.") { TargetStatus = status };
            }

            html = Decode(body, response.Content.Headers.ContentType);
            bytes = Encoding.UTF8.GetByteCount(html);
        }

        var elapsed = (long)_time.GetElapsedTime(started).TotalMilliseconds;
        return new RenderedPage(url, status, headers, contentType, html, bytes, false, elapsed, elapsed, 0);
    }

    private RenderedPage Denied(Uri url, long started)
    {
        var elapsed = (long)_time.GetElapsedTime(started).TotalMilliseconds;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-smokescreen-error"] = "CONNECT refused" };
        return new RenderedPage(url, 407, headers, null, "", 0, false, elapsed, elapsed, 0);
    }

    /// <summary>Lower-case names, as the browser path's headers are; repeated values joined.</summary>
    private static Dictionary<string, string> Headers(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            headers[name.ToLowerInvariant()] = string.Join(", ", values);
        }

        return headers;
    }

    private static async Task<byte[]> ReadAsync(HttpContent content, int limit, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length < limit && (read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit - buffer.Length)), ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>The header's charset, else a <c>&lt;meta charset&gt;</c> near the top, else UTF-8.</summary>
    private static string Decode(byte[] body, MediaTypeHeaderValue? type)
    {
        var name = type?.CharSet?.Trim('"');
        if (string.IsNullOrEmpty(name))
        {
            var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 2048));
            name = MetaCharset.Match(head) is { Success: true } match ? match.Groups[1].Value : null;
        }

        Encoding encoding;
        try
        {
            encoding = name is null ? Encoding.UTF8 : Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return encoding.GetString(body);
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public void Dispose() => _client.Dispose();

    // Host only, never the URL: it may carry secrets.
    [LoggerMessage(Level = LogLevel.Debug, Message = "HTTP-first attempt for {Host} gave no answer ({Reason}); rendering in the browser")]
    private partial void LogFellBack(string host, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HTTP-first attempt for {Host} failed ({Error}); rendering in the browser")]
    private partial void LogTransportFailed(string host, HttpRequestError error);
}
