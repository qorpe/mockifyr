namespace Mockifyr.Core;

/// <summary>
/// How large a request body this host will read, overall and per tenant (#349).
/// </summary>
/// <remarks>
/// <para>
/// Kestrel's own default (~30 MB) applies to every caller equally, which is fine on a laptop and not
/// fine once the host is reachable by people you do not employ: a partner can post that much per
/// request, repeatedly, with only the request quota between it and the machine.
/// </para>
/// <para>
/// The host value is a <b>ceiling</b>, not a default. A per-tenant value above it is clamped rather
/// than honoured — otherwise the one number an operator sets to bound the machine could be raised by
/// any tenant configuration written later, which is not a ceiling at all.
/// </para>
/// </remarks>
public sealed class RequestBodyLimits
{
    private readonly Dictionary<string, long> _perTenant = new(StringComparer.Ordinal);

    private RequestBodyLimits(long? hostCeiling)
    {
        HostCeiling = hostCeiling;
    }

    /// <summary>No limit configured — Kestrel's default applies, exactly as before.</summary>
    public static RequestBodyLimits Unset { get; } = new(null);

    /// <summary>The host-wide ceiling in bytes, or null when none was configured.</summary>
    public long? HostCeiling { get; }

    /// <summary>Whether any limit was configured at all.</summary>
    public bool IsConfigured => HostCeiling is not null || _perTenant.Count > 0;

    /// <summary>
    /// Builds the limits. <paramref name="perTenant"/> entries are <c>tenant:bytes</c>; anything that
    /// does not parse, or is not positive, is dropped rather than treated as zero — a limit of zero
    /// would refuse every request with a body, which is never what a typo meant.
    /// </summary>
    public static RequestBodyLimits From(long? hostCeiling, IEnumerable<string>? perTenant = null)
    {
        var limits = new RequestBodyLimits(hostCeiling is > 0 ? hostCeiling : null);
        foreach (var raw in perTenant ?? [])
        {
            var separator = raw.LastIndexOf(':');
            if (separator <= 0 || !long.TryParse(raw[(separator + 1)..], out var bytes) || bytes <= 0)
            {
                continue;
            }

            var tenant = raw[..separator].Trim();
            if (tenant.Length > 0)
            {
                limits._perTenant[tenant] = bytes;
            }
        }

        return limits;
    }

    /// <summary>
    /// The effective limit for a tenant, or null when nothing bounds it. A tenant value is clamped to
    /// <see cref="HostCeiling"/>; a tenant with no value of its own inherits the ceiling.
    /// </summary>
    public long? For(TenantId tenant)
    {
        if (!_perTenant.TryGetValue(tenant.Value, out var own))
        {
            return HostCeiling;
        }

        return HostCeiling is { } ceiling ? Math.Min(own, ceiling) : own;
    }

    /// <summary>
    /// The refusal for a body that is too large, naming <em>which</em> limit was hit so an operator
    /// knows whether to raise the tenant's value or the host's.
    /// </summary>
    public string Refusal(TenantId tenant, long limit) =>
        _perTenant.TryGetValue(tenant.Value, out var own) && own <= (HostCeiling ?? long.MaxValue)
            ? $"request body exceeds the limit for tenant '{tenant.Value}' ({limit} bytes)"
            : $"request body exceeds this host's limit ({limit} bytes)";
}
