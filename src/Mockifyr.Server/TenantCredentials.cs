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

    private readonly List<(string Tenant, byte[] Expected)> _principals = [];

    private TenantCredentials(IEnumerable<(string Tenant, string Header)> parsed)
    {
        foreach (var (tenant, header) in parsed)
        {
            _principals.Add((tenant, Encoding.UTF8.GetBytes(header)));
        }
    }

    /// <summary>True when no per-tenant credential was configured — the host then behaves as before.</summary>
    public bool IsEmpty => _principals.Count == 0;

    /// <summary>How many credentials were configured (for the startup line).</summary>
    public int Count => _principals.Count;

    /// <summary>
    /// Reads every <c>--tenant-credential</c> occurrence off the raw command line. Read from argv
    /// rather than configuration because .NET configuration keeps only the LAST value of a repeated
    /// key, which would silently drop every tenant but one.
    /// </summary>
    public static TenantCredentials Parse(string[] args)
    {
        var parsed = new List<(string, string)>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], Flag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // tenant:user:pass — the password may itself contain ':', so split only twice.
            var parts = args[i + 1].Split(':', 3);
            if (parts.Length == 3 && parts.All(part => part.Length > 0))
            {
                parsed.Add((parts[0], "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{parts[1]}:{parts[2]}"))));
            }
        }

        return new TenantCredentials(parsed);
    }

    /// <summary>
    /// The tenant a presented Authorization header is scoped to, or null when the header belongs to
    /// no configured per-tenant principal (the system credential, or none at all).
    /// </summary>
    public string? TenantFor(string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader))
        {
            return null;
        }

        var presented = Encoding.UTF8.GetBytes(authorizationHeader);
        foreach (var (tenant, expected) in _principals)
        {
            if (CryptographicOperations.FixedTimeEquals(presented, expected))
            {
                return tenant;
            }
        }

        return null;
    }
}
