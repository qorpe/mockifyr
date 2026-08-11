using System.Security.Cryptography;
using System.Text;

namespace Mockifyr.Server;

/// <summary>
/// Per-tenant admin credentials (#224): <c>--tenant-credential &lt;tenant&gt;:&lt;user&gt;:&lt;pass&gt;</c>,
/// repeatable. Tenant scoping is structural everywhere in the engine, but the <c>TenantId</c> itself
/// arrives in a client header — so without this, any admin caller can address any tenant by renaming
/// the header. Mapping a credential to exactly one tenant turns that header from a claim into an
/// authorization decision. Comparison is constant-time, matching the global admin credential.
/// </summary>
public sealed class TenantCredentials
{
    /// <summary>The repeatable flag, in the form <c>tenant:user:pass</c>.</summary>
    public const string Flag = "--tenant-credential";

    /// <summary>
    /// The partner form of the same flag (#346): identical tenant scoping, plus a refusal on every
    /// route and every stub field through which the host would act on the network.
    /// </summary>
    /// <remarks>
    /// A separate flag rather than a suffix on the existing one. The password may contain ':', so the
    /// existing value is split exactly twice and there is no fourth field to add without making some
    /// passwords unspellable — and a second flag makes "today's --tenant-credential is unchanged" true
    /// by construction rather than by careful reading.
    /// </remarks>
    public const string PartnerFlag = "--partner-credential";

    private readonly List<(string Tenant, bool Partner, byte[] Expected)> _principals = [];

    private TenantCredentials(IEnumerable<(string Tenant, bool Partner, string Header)> parsed)
    {
        foreach (var (tenant, partner, header) in parsed)
        {
            _principals.Add((tenant, partner, Encoding.UTF8.GetBytes(header)));
        }
    }

    /// <summary>True when no per-tenant credential was configured — the host then behaves as before.</summary>
    public bool IsEmpty => _principals.Count == 0;

    /// <summary>How many credentials were configured (for the startup line).</summary>
    public int Count => _principals.Count;

    /// <summary>How many of them are partner principals (for the startup line).</summary>
    public int PartnerCount => _principals.Count(principal => principal.Partner);

    /// <summary>
    /// Reads every <c>--tenant-credential</c> occurrence off the raw command line. Read from argv
    /// rather than configuration because .NET configuration keeps only the LAST value of a repeated
    /// key, which would silently drop every tenant but one.
    /// </summary>
    public static TenantCredentials Parse(string[] args)
    {
        var parsed = new List<(string, bool, string)>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            var partner = string.Equals(args[i], PartnerFlag, StringComparison.OrdinalIgnoreCase);
            if (!partner && !string.Equals(args[i], Flag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // tenant:user:pass — the password may itself contain ':', so split only twice.
            var parts = args[i + 1].Split(':', 3);
            if (parts.Length == 3 && parts.All(part => part.Length > 0))
            {
                parsed.Add((parts[0], partner, "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{parts[1]}:{parts[2]}"))));
            }
        }

        return new TenantCredentials(parsed);
    }

    /// <summary>
    /// The tenant a presented Authorization header is scoped to, or null when the header belongs to
    /// no configured per-tenant principal (the system credential, or none at all).
    /// </summary>
    public string? TenantFor(string? authorizationHeader) => PrincipalFor(authorizationHeader)?.Tenant;

    /// <summary>
    /// The principal a presented Authorization header belongs to — its tenant and whether it is a
    /// partner — or null when the header belongs to no configured per-tenant principal.
    /// </summary>
    public TenantPrincipal? PrincipalFor(string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader))
        {
            return null;
        }

        var presented = Encoding.UTF8.GetBytes(authorizationHeader);
        foreach (var (tenant, partner, expected) in _principals)
        {
            if (CryptographicOperations.FixedTimeEquals(presented, expected))
            {
                return new TenantPrincipal(tenant, partner);
            }
        }

        return null;
    }
}

/// <summary>
/// A per-tenant admin principal: the tenant it may address, and whether it is a partner — a class
/// that may do everything within its tenant's data and nothing that makes the host act outward (#346).
/// </summary>
public sealed record TenantPrincipal(string Tenant, bool IsPartner);
