namespace PinkRooster.WebLens.Search;

/// <summary>A configured instance with its endpoints resolved once, at start.</summary>
internal sealed record SearchInstance(string Name, Uri SearchUri, Uri ConfigUri, int Priority, IReadOnlyDictionary<string, string> Headers)
{
    public static SearchInstance From(SearchInstanceOptions options)
    {
        var raw = options.BaseUri.EndsWith('/') ? options.BaseUri : options.BaseUri + "/";
        var baseUri = new Uri(raw, UriKind.Absolute);
        return new SearchInstance(
            options.Name,
            new Uri(baseUri, "search"),
            new Uri(baseUri, "config"),
            options.Priority,
            new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Host only: an http base URI answered with a redirect to https changes scheme and port but not the instance.</summary>
    public bool SameHost(Uri other) => string.Equals(SearchUri.Host, other.Host, StringComparison.OrdinalIgnoreCase);
}
