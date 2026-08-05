namespace Mockifyr.Core;

/// <summary>
/// What time a tenant's stubs think it is (#290).
/// </summary>
/// <remarks>
/// <para>
/// Every date-dependent integration is otherwise testable only in real time: a token that expires in
/// an hour, a statement that closes at month end, a trial that ends in fourteen days. The usual
/// workarounds are to make the test wait or to make the stub lie with hardcoded dates that rot, and
/// both are worse than the feature.
/// </para>
/// <para>
/// The two modes are deliberately exclusive rather than composable. <see cref="FrozenAt"/> stops the
/// clock at an instant; <see cref="Offset"/> keeps it running, shifted. "Frozen <em>and</em> shifted"
/// has one meaning to whoever wrote it and another to whoever reads it later, so a request carrying
/// both is refused rather than interpreted — stepping a frozen clock forward is a new
/// <see cref="FrozenAt"/>, which says what it means.
/// </para>
/// </remarks>
/// <param name="FrozenAt">The instant time stands still at, or null to keep it running.</param>
/// <param name="Offset">How far the running clock is shifted; ignored when frozen.</param>
public sealed record ClockOverride(DateTimeOffset? FrozenAt, TimeSpan Offset)
{
    /// <summary>The default: the host's own clock, unmodified.</summary>
    public static readonly ClockOverride RealTime = new(null, TimeSpan.Zero);

    /// <summary>True when this override changes nothing — the fast path, and the "no clock set" answer.</summary>
    public bool IsRealTime => FrozenAt is null && Offset == TimeSpan.Zero;

    /// <summary>What this tenant sees, given what the host's clock says.</summary>
    public DateTimeOffset Apply(DateTimeOffset realNow) => FrozenAt ?? realNow + Offset;
}

/// <summary>
/// The tenant-scoped clock overrides, as the admin path sees them. Every entry point takes an
/// explicit <see cref="TenantId"/> (CLAUDE.md §2.6) — one tenant travelling in time must never move
/// another's.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> persisted and not carried on the change feed: a clock override is a
/// test-time instrument, and a host that came back from a restart still believing it is 2027 would be
/// a mystery rather than a convenience. It is announced in the same breath as it is set.
/// </remarks>
public interface IClockStore
{
    /// <summary>The tenant's override, or <see cref="ClockOverride.RealTime"/> when none is set.</summary>
    ClockOverride Get(TenantId tenant);

    /// <summary>Sets the tenant's override. <see cref="ClockOverride.RealTime"/> clears it.</summary>
    void Set(TenantId tenant, ClockOverride clock);

    /// <summary>Returns the tenant to real time.</summary>
    void Clear(TenantId tenant);
}

/// <summary>
/// The serve-path view of <see cref="IClockStore"/>: the single lookup templating needs per render.
/// Kept separate from the store so the hot path cannot accidentally mutate, and so a facade with no
/// clock configured can supply nothing at all.
/// </summary>
public interface IClockResolver
{
    /// <summary>The instant this tenant's templates should see.</summary>
    DateTimeOffset UtcNow(TenantId tenant);
}
