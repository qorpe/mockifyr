namespace Mockifyr.Core;

/// <summary>
/// How badly a whole dependency is behaving, for a tenant (#289).
/// </summary>
/// <remarks>
/// <para>
/// <c>delay</c> and <c>fault</c> are per-stub directives, which is the right shape for "this endpoint
/// is slow" and the wrong one for the question integration teams actually ask: <em>what does my system
/// do when this dependency degrades?</em> Answering that today means editing every stub in the tenant
/// and then editing them all back, so nobody does it and the resilience test never happens.
/// </para>
/// <para>
/// A profile composes with whatever each stub already declares rather than replacing it: a stub that
/// asks for 50 ms still gets 50 ms, plus whatever the dependency is adding today.
/// </para>
/// </remarks>
/// <param name="FixedDelayMs">Latency added to every response, before jitter.</param>
/// <param name="JitterMs">Upper bound of an additional uniform delay; 0 for a flat latency.</param>
/// <param name="ErrorRatio">Share of requests answered with <paramref name="ErrorStatus"/> (0..1).</param>
/// <param name="ErrorStatus">The status a degraded response carries.</param>
/// <param name="FaultRatio">Share of requests that fail at the connection instead (0..1).</param>
/// <param name="Fault">Which connection-level failure to emit.</param>
/// <param name="Seed">
/// Makes the sequence reproducible. Always present on a stored profile: one is generated when the
/// caller does not supply it and reported back, so a run that turns up something interesting can be
/// replayed rather than described.
/// </param>
public sealed record DegradationProfile(
    int FixedDelayMs,
    int JitterMs,
    double ErrorRatio,
    int ErrorStatus,
    double FaultRatio,
    FaultKind Fault,
    int Seed)
{
    /// <summary>A healthy dependency: the default, and what <c>DELETE</c> restores.</summary>
    public static readonly DegradationProfile Healthy =
        new(0, 0, 0d, 503, 0d, FaultKind.ConnectionResetByPeer, 0);

    /// <summary>True when this profile does nothing — the fast path, and the "not degraded" answer.</summary>
    public bool IsHealthy => FixedDelayMs <= 0 && JitterMs <= 0 && ErrorRatio <= 0d && FaultRatio <= 0d;
}

/// <summary>What a degradation profile decided to do to one request (#289).</summary>
/// <param name="DelayMs">Milliseconds to wait before answering, on top of the stub's own delay.</param>
/// <param name="ErrorStatus">Answer with this status and an empty body instead of the stub's response.</param>
/// <param name="Fault">Break the connection this way instead of answering at all.</param>
public readonly record struct DegradationDecision(int DelayMs, int? ErrorStatus, FaultKind? Fault)
{
    /// <summary>Serve normally.</summary>
    public static readonly DegradationDecision None = new(0, null, null);
}

/// <summary>
/// The pure decision: given a profile and which request this is, what happens (#289).
/// </summary>
/// <remarks>
/// <para>
/// Deterministic by construction. The outcome is a function of the seed and the request's ordinal, so
/// the same profile replays the same sequence — which is what turns a chaos experiment into a
/// regression test. Under concurrent traffic the ordinals are still handed out in arrival order; what
/// varies is which request gets which ordinal, not what ordinal <em>n</em> receives.
/// </para>
/// <para>
/// A connection fault outranks an error status: a dependency that resets the connection does not first
/// politely explain itself with a 503.
/// </para>
/// </remarks>
public static class DegradationPlan
{
    /// <summary>Decides what happens to request number <paramref name="ordinal"/> under this profile.</summary>
    public static DegradationDecision For(DegradationProfile profile, long ordinal)
    {
        if (profile.IsHealthy)
        {
            return DegradationDecision.None;
        }

        // Three independent draws from one hash: whether the connection fails, whether the response is
        // an error, and how much jitter to add. Deriving them from the same (seed, ordinal) keeps the
        // whole decision reproducible from two numbers a human can write down.
        var (faultDraw, errorDraw, jitterDraw) = Draws(profile.Seed, ordinal);

        var delay = profile.FixedDelayMs > 0 ? profile.FixedDelayMs : 0;
        if (profile.JitterMs > 0)
        {
            delay += (int)(jitterDraw * profile.JitterMs);
        }

        if (profile.FaultRatio > 0d && faultDraw < profile.FaultRatio)
        {
            return new DegradationDecision(delay, null, profile.Fault);
        }

        return profile.ErrorRatio > 0d && errorDraw < profile.ErrorRatio
            ? new DegradationDecision(delay, profile.ErrorStatus, null)
            : new DegradationDecision(delay, null, null);
    }

    /// <summary>
    /// Three uniform values in [0,1) from a seed and an ordinal. SplitMix64 finishing: cheap, no state
    /// to share between threads, and well-distributed enough that a 5% error ratio is 5% rather than
    /// approximately 5% over a run a test can afford.
    /// </summary>
    private static (double Fault, double Error, double Jitter) Draws(int seed, long ordinal)
    {
        var basis = (ulong)seed * 0x9E3779B97F4A7C15UL ^ (ulong)ordinal;
        return (Uniform(basis + 1), Uniform(basis + 2), Uniform(basis + 3));
    }

    private static double Uniform(ulong state)
    {
        var z = state + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;

        // 53 bits is the mantissa of a double, so this covers [0,1) without clumping at the ends.
        return (z >> 11) * (1.0 / 9007199254740992.0);
    }
}

/// <summary>
/// The tenant-scoped degradation profiles, as the admin path sees them (#289). Tenant-explicit
/// everywhere (CLAUDE.md §2.6): degrading a shared host for everybody is exactly the failure this
/// feature exists to avoid, and it is why the profile lives here rather than in a sidecar that cannot
/// tell one tenant's traffic from another's.
/// </summary>
/// <remarks>
/// In memory and not persisted, like the tenant clock: a host that came back from a restart still
/// dropping 5% of requests would be a support ticket, not a convenience.
/// </remarks>
public interface IDegradationStore
{
    /// <summary>The tenant's profile, or <see cref="DegradationProfile.Healthy"/> when none is set.</summary>
    DegradationProfile Get(TenantId tenant);

    /// <summary>Sets the tenant's profile. A healthy profile clears it.</summary>
    void Set(TenantId tenant, DegradationProfile profile);

    /// <summary>Returns the tenant to full health.</summary>
    void Clear(TenantId tenant);
}

/// <summary>
/// The serve-path view of <see cref="IDegradationStore"/>: one call per request that both takes the
/// next ordinal and decides. Kept separate from the store so the hot path cannot enumerate or mutate.
/// </summary>
public interface IDegradationResolver
{
    /// <summary>What happens to the request being served right now.</summary>
    DegradationDecision Next(TenantId tenant);
}
