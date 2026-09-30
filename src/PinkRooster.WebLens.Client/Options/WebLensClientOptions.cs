using Microsoft.Extensions.Options;

namespace PinkRooster.WebLens.Client;

/// <summary>
/// Configuration options for connecting to a WebLens API host.
/// </summary>
public sealed class WebLensClientOptions
{
    public const string ConfigurationSection = "WebLens:Client";

    /// <summary>
    /// Base address of the WebLens host (e.g. https://weblens.example.com/).
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// API key for authentication via the <c>X-Api-Key</c> header.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Overall request timeout. Defaults to 60 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum number of retry attempts for transient errors (502, 503, 504, 429). Defaults to 3.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Initial backoff delay for retries. Defaults to 500 ms.
    /// </summary>
    public TimeSpan RetryInitialDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Whether built-in resilience and retries are enabled. Defaults to true.
    /// </summary>
    public bool EnableResilience { get; set; } = true;

    /// <summary>
    /// Validates the options for direct instantiation.
    /// </summary>
    public void Validate()
    {
        if (BaseAddress is null)
        {
            throw new ArgumentException("BaseAddress must be specified.", nameof(BaseAddress));
        }

        if (!BaseAddress.IsAbsoluteUri || (BaseAddress.Scheme != Uri.UriSchemeHttp && BaseAddress.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("BaseAddress must be an absolute http or https URI.", nameof(BaseAddress));
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException("ApiKey must be specified.", nameof(ApiKey));
        }

        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Timeout must be greater than zero.", nameof(Timeout));
        }

        if (MaxRetries < 0)
        {
            throw new ArgumentException("MaxRetries must be greater than or equal to zero.", nameof(MaxRetries));
        }

        if (RetryInitialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentException("RetryInitialDelay must be greater than zero.", nameof(RetryInitialDelay));
        }
    }
}

/// <summary>
/// Microsoft.Extensions.Options validator for <see cref="WebLensClientOptions"/>.
/// </summary>
public sealed class WebLensClientOptionsValidator : IValidateOptions<WebLensClientOptions>
{
    public ValidateOptionsResult Validate(string? name, WebLensClientOptions options)
    {
        var errors = new List<string>();

        if (options.BaseAddress is null)
        {
            errors.Add("BaseAddress must be specified.");
        }
        else if (!options.BaseAddress.IsAbsoluteUri || (options.BaseAddress.Scheme != Uri.UriSchemeHttp && options.BaseAddress.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("BaseAddress must be an absolute http or https URI.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            errors.Add("ApiKey must be specified.");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            errors.Add("Timeout must be greater than zero.");
        }

        if (options.MaxRetries < 0)
        {
            errors.Add("MaxRetries must be greater than or equal to zero.");
        }

        if (options.RetryInitialDelay <= TimeSpan.Zero)
        {
            errors.Add("RetryInitialDelay must be greater than zero.");
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}
