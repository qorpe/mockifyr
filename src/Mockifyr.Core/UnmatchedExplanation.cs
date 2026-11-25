namespace Mockifyr.Core;

/// <summary>
/// Which tenants are answered with an explanation when nothing matched (#457): <c>--explain-unmatched
/// &lt;tenant&gt;</c>, repeatable. Off for every tenant unless named.
/// </summary>
/// <remarks>
/// <para>
/// Off by default, and per tenant rather than host-wide, for two reasons. A real API does not tell its
/// caller why it has no such route, so a sandbox that rehearses production must be able to stay bare.
/// And the bare 404 is what the differential suite pins against the reference engine (#288): every tenant
/// that is not named keeps exactly that response, byte for byte.
/// </para>
/// <para>
/// Pure: a set of names, nothing else. The serving facade asks it once per unmatched request.
/// </para>
/// </remarks>
public sealed class UnmatchedExplanation
{
    private readonly HashSet<string> _tenants = new(StringComparer.Ordinal);

    private UnmatchedExplanation()
    {
    }

    /// <summary>Whether any tenant is named.</summary>
    public bool IsConfigured => _tenants.Count > 0;

    /// <summary>The named tenants, in ordinal order — what the startup line prints.</summary>
    public IReadOnlyList<string> Tenants => [.. _tenants.Order(StringComparer.Ordinal)];

    /// <summary>Builds the set from the flag's values; a blank entry names nobody.</summary>
    public static UnmatchedExplanation From(IEnumerable<string>? tenants)
    {
        var explanation = new UnmatchedExplanation();
        foreach (var tenant in tenants ?? [])
        {
            if (!string.IsNullOrWhiteSpace(tenant))
            {
                explanation._tenants.Add(tenant.Trim());
            }
        }

        return explanation;
    }

    /// <summary>Whether an unmatched request of <paramref name="tenant"/> is answered with an explanation.</summary>
    public bool For(TenantId tenant) => _tenants.Contains(tenant.Value);
}
