namespace Mockifyr.Core;

/// <summary>
/// Which browser origins may call a tenant's sandbox (#349).
/// </summary>
/// <remarks>
/// <para>
/// Off by default, and absent entirely until configured: a host with no origins behaves exactly as it
/// always has, emitting no CORS headers at all. This is the first wall anybody hits integrating a web
/// front end against a sandbox, and it looks like our bug — but turning it on for everyone by default
/// would hand every browser on the internet a credentialed path into somebody's tenant.
/// </para>
/// <para>
/// Origins are matched whole — scheme, host and port — because that is what an <c>Origin</c> header
/// is and what the browser compares. <c>https://app.example</c> does not cover
/// <c>http://app.example</c>, and it should not: the two are different security contexts, and
/// treating them as one is how a mixed-content mistake becomes a permission.
/// </para>
/// </remarks>
public sealed class CorsOrigins
{
    private readonly HashSet<string> _hostWide = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _perTenant = new(StringComparer.Ordinal);

    private CorsOrigins()
    {
    }

    /// <summary>Nothing configured — no CORS headers are emitted anywhere.</summary>
    public static CorsOrigins None { get; } = new();

    /// <summary>Whether any origin was configured at all.</summary>
    public bool IsConfigured => _hostWide.Count > 0 || _perTenant.Count > 0;

    /// <summary>
    /// Builds the policy. <paramref name="hostWide"/> entries are origins; <paramref name="perTenant"/>
    /// entries are <c>tenant=origin</c>.
    /// </summary>
    /// <remarks>
    /// The per-tenant separator is <c>=</c> rather than <c>:</c>, because every origin contains a colon
    /// (<c>https://…</c>) and a rule that has to explain which colon it means is a rule people get
    /// wrong. A tenant with its own list uses it; one without inherits the host-wide list.
    /// </remarks>
    public static CorsOrigins From(IEnumerable<string>? hostWide, IEnumerable<string>? perTenant = null)
    {
        var policy = new CorsOrigins();
        foreach (var origin in hostWide ?? [])
        {
            if (Normalize(origin) is { } usable)
            {
                policy._hostWide.Add(usable);
            }
        }

        foreach (var raw in perTenant ?? [])
        {
            var separator = raw.IndexOf('=');
            if (separator <= 0 || Normalize(raw[(separator + 1)..]) is not { } usable)
            {
                continue;
            }

            var tenant = raw[..separator].Trim();
            if (tenant.Length == 0)
            {
                continue;
            }

            if (!policy._perTenant.TryGetValue(tenant, out var set))
            {
                policy._perTenant[tenant] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            set.Add(usable);
        }

        return policy;
    }

    /// <summary>
    /// Whether <paramref name="origin"/> may call <paramref name="tenant"/>'s sandbox. An unconfigured
    /// policy allows nothing — the caller then emits no headers rather than refusing, which is what
    /// leaves an existing host byte-identical.
    /// </summary>
    public bool Allows(TenantId tenant, string? origin)
    {
        if (Normalize(origin) is not { } usable)
        {
            return false;
        }

        return _perTenant.TryGetValue(tenant.Value, out var own)
            ? own.Contains(usable)
            : _hostWide.Contains(usable);
    }

    /// <summary>The configured origins, for the startup line.</summary>
    public IReadOnlyList<string> Describe() =>
    [
        .. _hostWide.OrderBy(o => o, StringComparer.Ordinal),
        .. _perTenant.OrderBy(p => p.Key, StringComparer.Ordinal)
            .SelectMany(p => p.Value.OrderBy(o => o, StringComparer.Ordinal).Select(o => $"{p.Key}={o}")),
    ];

    /// <summary>
    /// An origin reduced to what a browser actually sends: scheme + host + port, no trailing slash and
    /// no path. Null when it is not a usable absolute origin.
    /// </summary>
    /// <remarks>
    /// Configuration is written by hand, and <c>https://app.example/</c> with a trailing slash never
    /// matches an <c>Origin</c> header. Normalising here means a reasonable-looking entry works rather
    /// than failing in a way that looks like the feature is broken.
    /// </remarks>
    private static string? Normalize(string? origin)
    {
        var text = origin?.Trim();
        if (string.IsNullOrEmpty(text)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        return uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }
}
