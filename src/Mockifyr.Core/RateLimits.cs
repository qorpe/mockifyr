namespace Mockifyr.Core;

/// <summary>
/// One rate-limit window: how many requests over how long (#354).
/// </summary>
/// <remarks>
/// Real sandboxes publish a burst limit <em>and</em> a sustained one because they protect different
/// things — a hundred requests in a second is a runaway loop, and a hundred thousand in a day is a
/// consumer who should be paying. One number cannot say both.
/// </remarks>
public sealed record RateWindow(TimeSpan Duration, int Limit)
{
    /// <summary>The window this request falls in, aligned to the epoch so every replica agrees.</summary>
    /// <remarks>
    /// Aligned rather than sliding-from-first-request: two hosts that started at different times must
    /// bucket the same instant identically, or a shared counter is shared in name only.
    /// </remarks>
    public long BucketFor(DateTimeOffset now) => now.ToUnixTimeSeconds() / (long)Duration.TotalSeconds;

    /// <summary>When the current window ends.</summary>
    public DateTimeOffset ResetAt(DateTimeOffset now) =>
        DateTimeOffset.FromUnixTimeSeconds((BucketFor(now) + 1) * (long)Duration.TotalSeconds);
}

/// <summary>
/// A shared, atomic counter of requests per key and window (#354).
/// </summary>
/// <remarks>
/// The seam that makes a quota mean the same number behind two replicas as behind one. In-process is
/// the default — a laptop must not need Redis to run a sandbox — and Redis is a second use of a
/// provider this project already ships rather than new infrastructure.
/// </remarks>
public interface IRateCounter
{
    /// <summary>
    /// Counts one request and returns the new total for that key in that window. Must be atomic:
    /// two replicas incrementing at once have to see 1 and 2, never 1 and 1.
    /// </summary>
    int Increment(string key, RateWindow window, DateTimeOffset now);

    /// <summary>The current total without counting a request, for reporting usage.</summary>
    int Peek(string key, RateWindow window, DateTimeOffset now);
}

/// <summary>
/// Evaluates a key's request against every window it is subject to, and reports the one that binds
/// (#354).
/// </summary>
public static class RateLimits
{
    /// <summary>
    /// Counts one request against all <paramref name="windows"/> and returns the binding decision.
    /// An empty window set is unlimited.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every window is counted even when an earlier one already refused. Counting only until the first
    /// refusal would let a caller who is over their burst limit spend the rest of the day under the
    /// radar of the sustained one — the two windows have to see the same traffic.
    /// </para>
    /// <para>
    /// When more than one window refuses, the reported reset is the <b>latest</b> of them. Retrying
    /// after the burst window reopens would still fail the daily budget, and a <c>Retry-After</c> that
    /// is too short is worse than none: it invites a client to hammer a door that is still shut.
    /// </para>
    /// <para>
    /// When nothing refuses, the reported window is the one with the least left — the limit the caller
    /// is about to meet, which is the useful thing to put in a header.
    /// </para>
    /// </remarks>
    public static QuotaDecision Count(
        string keyId,
        IReadOnlyList<RateWindow> windows,
        IRateCounter counter,
        DateTimeOffset now)
    {
        if (windows.Count == 0)
        {
            return new QuotaDecision(Allowed: true, Limit: 0, Remaining: 0, now);
        }

        QuotaDecision? refused = null;
        QuotaDecision? tightest = null;

        foreach (var window in windows)
        {
            if (window.Limit <= 0 || window.Duration <= TimeSpan.Zero)
            {
                continue;
            }

            var used = counter.Increment(keyId, window, now);
            var remaining = Math.Max(0, window.Limit - used);
            var decision = new QuotaDecision(used <= window.Limit, window.Limit, remaining, window.ResetAt(now));

            if (!decision.Allowed)
            {
                refused = refused is null || decision.ResetAt > refused.ResetAt ? decision : refused;
            }

            tightest = tightest is null || decision.Remaining < tightest.Remaining ? decision : tightest;
        }

        return refused ?? tightest ?? new QuotaDecision(Allowed: true, Limit: 0, Remaining: 0, now);
    }

    /// <summary>
    /// The windows a key is subject to, from its hourly quota plus any host-wide burst ceiling.
    /// </summary>
    /// <remarks>
    /// The per-key hourly number is the one operators already configured, so it keeps meaning exactly
    /// what it meant. A burst ceiling is host-level because it protects the host, not the consumer's
    /// budget — and making every key restate it would leave the one key nobody updated as the way in.
    /// </remarks>
    public static IReadOnlyList<RateWindow> For(int? quotaPerHour, RateWindow? burst)
    {
        var windows = new List<RateWindow>(2);
        if (quotaPerHour is > 0 and { } hourly)
        {
            windows.Add(new RateWindow(TimeSpan.FromHours(1), hourly));
        }

        // A burst ceiling applies even to a key with no hourly quota: "unlimited" is a budget
        // statement, not permission to melt the host.
        if (burst is { Limit: > 0, Duration.TotalSeconds: > 0 })
        {
            // Two windows of the same length are one window, and the tighter limit is the one that
            // means anything. Not a nicety: a counter identifies a bucket by key and duration, so
            // leaving both in place would count every request twice against the same bucket and
            // enforce half of what the operator wrote. `--rate-burst 600/3600` beside an hourly quota
            // is an entirely ordinary thing to configure.
            var sameLength = windows.FindIndex(w => w.Duration == burst.Duration);
            if (sameLength >= 0)
            {
                windows[sameLength] = windows[sameLength].Limit <= burst.Limit ? windows[sameLength] : burst;
            }
            else
            {
                windows.Add(burst);
            }
        }

        return windows;
    }
}

/// <summary>
/// The default counter: in-process, correct for one host (#354). Thread-safe.
/// </summary>
/// <remarks>
/// Buckets expire lazily — a key's entry is replaced when its bucket rolls over, so the dictionary
/// holds one entry per key per window rather than growing with time.
/// </remarks>
public sealed class InMemoryRateCounter : IRateCounter
{
    private readonly Dictionary<(string Key, TimeSpan Window), (long Bucket, int Count)> _counts = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public int Increment(string key, RateWindow window, DateTimeOffset now)
    {
        var bucket = window.BucketFor(now);
        lock (_gate)
        {
            var slot = (key, window.Duration);
            var current = _counts.TryGetValue(slot, out var existing) && existing.Bucket == bucket
                ? existing.Count
                : 0;

            _counts[slot] = (bucket, current + 1);
            return current + 1;
        }
    }

    /// <inheritdoc />
    public int Peek(string key, RateWindow window, DateTimeOffset now)
    {
        var bucket = window.BucketFor(now);
        lock (_gate)
        {
            return _counts.TryGetValue((key, window.Duration), out var existing) && existing.Bucket == bucket
                ? existing.Count
                : 0;
        }
    }
}
