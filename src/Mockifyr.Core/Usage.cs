namespace Mockifyr.Core;

/// <summary>
/// What happened to one request made with a sandbox key (#356).
/// </summary>
/// <remarks>
/// The refusals are separate values rather than one "failed" because they send whoever is reading in
/// different directions: a wrong credential, a spent quota and a scope refusal are three different
/// conversations, and collapsing them turns "your calls are failing" into an afternoon.
/// </remarks>
public enum UsageOutcome
{
    /// <summary>A stub matched and was served.</summary>
    Matched = 0,

    /// <summary>Nothing matched — the integration is calling something the sandbox does not model.</summary>
    Unmatched = 1,

    /// <summary>The key was unknown, expired or revoked.</summary>
    Unauthorized = 2,

    /// <summary>The quota or burst ceiling refused it.</summary>
    RateLimited = 3,

    /// <summary>A read-only key attempted a write.</summary>
    Forbidden = 4,
}

/// <summary>
/// Records per-key traffic for usage reporting (#356).
/// </summary>
/// <remarks>
/// A seam rather than a call into a store, because the recording sits on the serving path: the host
/// decides whether anything is kept at all (<c>--usage</c>), and a host that keeps nothing pays one
/// virtual call for the decision.
/// </remarks>
public interface IUsageRecorder
{
    /// <summary>Counts one request. Never throws — usage reporting must not be able to fail a request.</summary>
    void Record(TenantId tenant, string keyId, string path, UsageOutcome outcome, DateTimeOffset now);

    /// <summary>What the tenant's keys did over the last <paramref name="hours"/> hours.</summary>
    IReadOnlyList<KeyUsage> Report(TenantId tenant, int hours, DateTimeOffset now);
}

/// <summary>One key's traffic over the reported window (#356).</summary>
public sealed record KeyUsage(
    string KeyId,
    int Total,
    int Matched,
    int Unmatched,
    int Unauthorized,
    int RateLimited,
    int Forbidden,
    IReadOnlyList<PathUsage> TopPaths,
    IReadOnlyList<PathUsage> TopUnmatchedPaths);

/// <summary>One path a key called, and how often (#356).</summary>
public sealed record PathUsage(string Path, int Count);

/// <summary>
/// The default recorder: in-process, bounded in every direction (#356).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> a second journal. Nothing but the path, the outcome and a count is kept —
/// no headers, no bodies, no timestamps per request — so the masking that keeps secrets out of the
/// journal (#227) cannot be walked around by reading usage instead.
/// </para>
/// <para>
/// Bounded three ways: one hour bucket per key for <see cref="RetainedHours"/> hours (older buckets
/// are dropped as they are passed), <see cref="TrackedPaths"/> distinct paths per bucket, and a cap on
/// the keys tracked at all. The path table is approximate on purpose — see
/// <see cref="PathCounter"/> — because an exact top-N needs every distinct path, which is precisely
/// the unbounded thing this must not become.
/// </para>
/// </remarks>
public sealed class InMemoryUsageRecorder : IUsageRecorder
{
    /// <summary>How many hourly buckets are kept per key — a day, so "yesterday afternoon" is answerable.</summary>
    public const int RetainedHours = 24;

    /// <summary>Distinct paths tracked per bucket per outcome class.</summary>
    public const int TrackedPaths = 50;

    /// <summary>How many keys are tracked at all, so an attacker cannot grow this by presenting keys.</summary>
    /// <remarks>
    /// Only ever reached by keys that <em>authenticated</em> — an unknown token is refused before it is
    /// recorded, so this bound guards against a tenant with a great many live keys, not against a
    /// stranger.
    /// </remarks>
    public const int TrackedKeys = 1000;

    private readonly Dictionary<(TenantId Tenant, string Key, long Hour), Bucket> _buckets = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public void Record(TenantId tenant, string keyId, string path, UsageOutcome outcome, DateTimeOffset now)
    {
        var hour = now.ToUnixTimeSeconds() / 3600;
        lock (_gate)
        {
            var slot = (tenant, keyId, hour);
            if (!_buckets.TryGetValue(slot, out var bucket))
            {
                // Evicting by age before refusing by count: a host that has been up for a week has
                // stale buckets to give back, and refusing a new key while holding yesterday's data
                // would report nothing for the key somebody is actually asking about.
                Evict(hour);
                if (_buckets.Count >= TrackedKeys * RetainedHours)
                {
                    return;
                }

                _buckets[slot] = bucket = new Bucket();
            }

            bucket.Count(outcome, path);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<KeyUsage> Report(TenantId tenant, int hours, DateTimeOffset now)
    {
        var oldest = now.ToUnixTimeSeconds() / 3600 - Math.Clamp(hours, 1, RetainedHours) + 1;
        lock (_gate)
        {
            return [.. _buckets
                .Where(entry => entry.Key.Tenant == tenant && entry.Key.Hour >= oldest)
                .GroupBy(entry => entry.Key.Key, StringComparer.Ordinal)
                .Select(group => Merge(group.Key, [.. group.Select(entry => entry.Value)]))
                .OrderByDescending(usage => usage.Total)];
        }
    }

    private void Evict(long currentHour)
    {
        foreach (var slot in _buckets.Keys.Where(key => key.Hour <= currentHour - RetainedHours).ToList())
        {
            _buckets.Remove(slot);
        }
    }

    private static KeyUsage Merge(string keyId, IReadOnlyList<Bucket> buckets)
    {
        var all = new PathCounter();
        var unmatched = new PathCounter();
        foreach (var bucket in buckets)
        {
            all.Absorb(bucket.Paths);
            unmatched.Absorb(bucket.UnmatchedPaths);
        }

        return new KeyUsage(
            keyId,
            buckets.Sum(b => b.Total),
            buckets.Sum(b => b.Matched),
            buckets.Sum(b => b.Unmatched),
            buckets.Sum(b => b.Unauthorized),
            buckets.Sum(b => b.RateLimited),
            buckets.Sum(b => b.Forbidden),
            all.Top(10),
            unmatched.Top(10));
    }

    private sealed class Bucket
    {
        public int Total { get; private set; }

        public int Matched { get; private set; }

        public int Unmatched { get; private set; }

        public int Unauthorized { get; private set; }

        public int RateLimited { get; private set; }

        public int Forbidden { get; private set; }

        public PathCounter Paths { get; } = new();

        public PathCounter UnmatchedPaths { get; } = new();

        public void Count(UsageOutcome outcome, string path)
        {
            Total++;
            switch (outcome)
            {
                case UsageOutcome.Matched: Matched++; break;
                case UsageOutcome.Unmatched: Unmatched++; break;
                case UsageOutcome.Unauthorized: Unauthorized++; break;
                case UsageOutcome.RateLimited: RateLimited++; break;
                case UsageOutcome.Forbidden: Forbidden++; break;
            }

            Paths.Add(path);
            if (outcome == UsageOutcome.Unmatched)
            {
                // Tracked separately rather than filtered later: the unmatched paths are the ones worth
                // reading, and a busy matched path would otherwise crowd them out of a bounded table.
                UnmatchedPaths.Add(path);
            }
        }
    }

    /// <summary>
    /// An approximate heavy-hitters counter over a fixed number of slots (Space-Saving).
    /// </summary>
    /// <remarks>
    /// When the table is full, the smallest entry is replaced and the newcomer inherits its count. The
    /// consequence, stated plainly: a count can overstate a rare path that arrived after a busy one was
    /// evicted, and the ordering of the true heavy hitters is what this is accurate about. Exactness
    /// would cost one entry per distinct path, which is the unbounded growth the whole design refuses.
    /// </remarks>
    internal sealed class PathCounter
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, int> Counts => _counts;

        public void Add(string path, int count = 1)
        {
            if (_counts.TryGetValue(path, out var existing))
            {
                _counts[path] = existing + count;
                return;
            }

            if (_counts.Count < TrackedPaths)
            {
                _counts[path] = count;
                return;
            }

            var smallest = _counts.MinBy(entry => entry.Value);
            _counts.Remove(smallest.Key);
            _counts[path] = smallest.Value + count;
        }

        public void Absorb(PathCounter other)
        {
            foreach (var (path, count) in other._counts)
            {
                Add(path, count);
            }
        }

        public IReadOnlyList<PathUsage> Top(int count) =>
            [.. _counts
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Take(count)
                .Select(entry => new PathUsage(entry.Key, entry.Value))];
    }
}

/// <summary>The no-op default: nothing is recorded unless the host asks for it (#356).</summary>
public sealed class NullUsageRecorder : IUsageRecorder
{
    /// <inheritdoc />
    public void Record(TenantId tenant, string keyId, string path, UsageOutcome outcome, DateTimeOffset now)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<KeyUsage> Report(TenantId tenant, int hours, DateTimeOffset now) => [];
}
