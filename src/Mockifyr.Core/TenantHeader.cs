namespace Mockifyr.Core;

/// <summary>
/// The request header that names the tenant (#396). Every transport reads the same one — HTTP, the
/// admin API, gRPC, WebSocket, the broker mapping surface and the SMS provider profile — so it is
/// declared once here rather than as a constant per facade.
/// </summary>
/// <remarks>
/// <para>
/// It is configurable because it is not an internal detail: an organisation running this platform
/// under its own name has the header in every example, every client and every runbook it writes. The
/// default is the historical value, so a host that sets nothing behaves exactly as it always did.
/// </para>
/// <para>
/// This lives in Core because all eight facades reference Core and nothing else in common — but it is
/// data, not behaviour, so the engine stays transport-agnostic: Core never reads a header, it only
/// knows what the operator decided to call one.
/// </para>
/// </remarks>
public sealed record TenantHeaderOptions
{
    /// <summary>The historical name, and the default.</summary>
    public const string DefaultName = "X-Mockifyr-Tenant";

    /// <summary>The header a request names its tenant in.</summary>
    public string Name { get; init; } = DefaultName;

    /// <summary>The unconfigured host's answer.</summary>
    public static TenantHeaderOptions Default { get; } = new();

    /// <summary>
    /// Whether a name is a legal HTTP field name (RFC 9110 §5.1 token).
    /// </summary>
    /// <remarks>
    /// Worth refusing rather than accepting: a header name containing a space or a colon is not
    /// rejected by the framework, it simply never matches. The host would start, every request would
    /// silently fall back to the default tenant, and the symptom — one tenant's stubs answering
    /// another's calls — points nowhere near a mistyped flag.
    /// </remarks>
    public static bool IsWellFormed(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var c in name)
        {
            var legal = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.'
                     or '^' or '_' or '`' or '|' or '~';
            if (!legal)
            {
                return false;
            }
        }

        return true;
    }
}
