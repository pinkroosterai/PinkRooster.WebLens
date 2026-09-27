using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PinkRooster.WebLens.Api.Errors;

namespace PinkRooster.WebLens.Api.Hosting;

/// <summary>The configured keys with their hashes decoded once. Read on first use, after start-up validation.</summary>
internal sealed class ApiKeyStore(IOptions<ApiOptions> options)
{
    private readonly Lazy<(ApiKeyOptions Key, byte[] Hash)[]> _keys =
        new(() => [.. options.Value.Keys.Select(k => (k, Convert.FromHexString(k.Sha256)))]);

    /// <summary>The key whose hash matches, compared in fixed time against every configured key.</summary>
    public ApiKeyOptions? Find(string presented)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        ApiKeyOptions? match = null;
        foreach (var (key, keyHash) in _keys.Value)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, keyHash))
            {
                match = key;
            }
        }

        return match;
    }
}

/// <summary>
/// The <c>ApiKey</c> scheme: <c>X-Api-Key</c>, hashed and compared in fixed time. Every failure gets the
/// same 401 body, whatever the cause; a valid key without the endpoint's scope gets 403. Neither logs the key. On
/// <c>/mcp</c> only, the key may also come as <c>Authorization: Bearer</c>, the form MCP clients' setup screens offer;
/// <c>/v1</c> keeps one form.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ApiKeyStore keys,
    IProblemDetailsService problems) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    private const string BearerPrefix = "Bearer ";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (PresentedKey() is not { } presented)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (keys.Find(presented) is not { } key)
        {
            return Task.FromResult(AuthenticateResult.Fail("The API key is not valid."));
        }

        var claims = new List<Claim> { new(Scopes.KeyIdClaimType, key.Id) };
        claims.AddRange(key.Scopes.Select(scope => new Claim(Scopes.ClaimType, scope)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, Scopes.KeyIdClaimType, null));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        Problems.WriteAsync(
            problems,
            Context,
            StatusCodes.Status401Unauthorized,
            ProblemTypes.Unauthorized,
            "A valid API key is required.",
            IsMcp ? "Send the key in the X-Api-Key header or as Authorization: Bearer <key>." : "Send the key in the X-Api-Key header.",
            "Unauthorized");

    private bool IsMcp => Request.Path.StartsWithSegments(McpEndpoint.Path, StringComparison.Ordinal);

    /// <summary>X-Api-Key wherever it is sent; the bearer form only on /mcp. Exactly one non-empty value, or none.</summary>
    private string? PresentedKey()
    {
        if (Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return values.Count == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;
        }

        if (IsMcp && Request.Headers.Authorization is { Count: 1 } authorization
            && authorization[0] is { } value && value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) && value.Length > BearerPrefix.Length)
        {
            return value[BearerPrefix.Length..].Trim();
        }

        return null;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Problems.WriteAsync(problems, Context, StatusCodes.Status403Forbidden, ProblemTypes.Forbidden, "The API key lacks the scope this endpoint needs.", null, "Forbidden");
}
