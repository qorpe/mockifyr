namespace Mockifyr.Server;

/// <summary>
/// What the dashboard calls itself (#396). Every field is optional, and an unset field means "use the
/// dashboard's own default" rather than "show nothing" — so a host that configures none of this looks
/// exactly as it always did.
/// </summary>
/// <remarks>
/// This lives in the host rather than in Core: it is not something the engine can act on, only
/// something the served shell carries to the browser. The engine never learns the product has a name.
/// </remarks>
public sealed record BrandOptions
{
    /// <summary>The product name shown beside the mark. Null keeps the dashboard's own.</summary>
    public string? Name { get; init; }

    /// <summary>The line under the name. Null keeps the dashboard's own, which is localised.</summary>
    public string? Subtitle { get; init; }

    /// <summary>Where the "report an issue" item points. Null keeps the project's own.</summary>
    public string? SupportUrl { get; init; }

    /// <summary>
    /// A logo file on disk, served under the dashboard prefix and used in place of the built-in mark.
    /// </summary>
    /// <remarks>
    /// Kept as a path rather than inlined data because a logo is an image an operator drops next to
    /// their values file, and mounting a file is the gesture Kubernetes already has for that.
    /// </remarks>
    public string? LogoPath { get; init; }

    /// <summary>Nothing configured — the shipped identity.</summary>
    public static BrandOptions Default { get; } = new();

    /// <summary>Whether anything at all was set.</summary>
    public bool IsDefault =>
        Name is null && Subtitle is null && SupportUrl is null && LogoPath is null;

    /// <summary>
    /// Whether a support URL is one a browser may be sent to. Only http(s): a `javascript:` or `data:`
    /// URL in an anchor the dashboard renders would be a scripting vector handed to the operator by
    /// their own configuration, which is a strange way to be compromised.
    /// </summary>
    public static bool IsUsableSupportUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
}
