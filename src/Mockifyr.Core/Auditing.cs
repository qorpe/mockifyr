namespace Mockifyr.Core;

/// <summary>
/// One recorded administrative change (#247). The request journal records what was *served*; this
/// records what was *changed* — the record a reviewer asks for first, and what makes a mistake
/// explainable afterwards.
/// </summary>
/// <param name="Id">Unique entry id.</param>
/// <param name="Timestamp">When the change was accepted.</param>
/// <param name="Principal">
/// Who did it, as a label — <c>system</c>, <c>tenant:acme</c>, <c>sandbox:mfk_ab12cd34</c> (prefix
/// only) or <c>anonymous</c>. Never a secret: an audit trail that leaks credentials is a liability,
/// not a control.
/// </param>
/// <param name="Tenant">The tenant the change was scoped to.</param>
/// <param name="Action">The operation, as <c>METHOD /path</c> (e.g. <c>POST /__admin/mappings</c>).</param>
/// <param name="Target">The addressed resource id when the route carried one, else null.</param>
/// <param name="Outcome">The HTTP status the operation answered with — refusals are audited too.</param>
public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    string Principal,
    TenantId Tenant,
    string Action,
    string? Target,
    int Outcome);

/// <summary>
/// Append-only, bounded audit storage (#247). Bounded for the same reason the journal is: an
/// unbounded in-memory log is a slow leak. Tenant-scoped reads, because one tenant's changes are not
/// another's business.
/// </summary>
public interface IAuditLog
{
    /// <summary>Appends an entry. Never throws — auditing must not fail the operation it describes.</summary>
    void Append(AuditEntry entry);

    /// <summary>The tenant's entries, newest first, at most <paramref name="limit"/> of them.</summary>
    IReadOnlyList<AuditEntry> Read(TenantId tenant, int limit);
}

/// <summary>Records nothing — the default until auditing is switched on.</summary>
public sealed class NullAuditLog : IAuditLog
{
    /// <inheritdoc />
    public void Append(AuditEntry entry)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<AuditEntry> Read(TenantId tenant, int limit) => [];
}
