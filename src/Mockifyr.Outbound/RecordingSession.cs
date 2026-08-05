using System.Text;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;

namespace Mockifyr.Outbound;

/// <summary>
/// The state of live recordings (G12d): while a tenant is recording, the mock-serving facade proxies
/// that tenant's requests to its target and records each exchange here. Shared as a singleton between
/// the admin endpoints (start/stop/snapshot) and the mock-serving fallback. Thread-safe.
/// <para>
/// State is per tenant. It used to be one global session, which meant a shared host could only ever
/// record for one team at a time — and worse, silently: starting a second recording discarded the
/// first team's captures, and every tenant's traffic was proxied to whichever target was set last.
/// Every entry point therefore takes an explicit <see cref="TenantId"/>, so forgetting to scope one
/// is a compile error rather than a cross-tenant leak (CLAUDE.md §2.6).
/// </para>
/// <para>
/// Stub generation happens at snapshot/stop time, because a repeat of the same request changes what
/// earlier captures mean: the oracle's recorder does not deduplicate repeats — it chains them into a
/// scenario (the first capture serves at <c>Started</c> and advances, each later one serves from the
/// prior state), so a replay yields the recorded responses in recorded order. Distinct requests stay
/// scenario-free. Oracle-verified; see docs/parity/g12-transport.md.
/// </para>
/// </summary>
public sealed class RecordingSession
{
    private sealed record Exchange(string Key, CanonicalRequest Request, CanonicalResponse Response);

    private sealed class TenantState
    {
        public List<Exchange> Exchanges { get; } = [];

        public string? Target { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<TenantId, TenantState> _byTenant = [];

    /// <summary>The upstream base URL this tenant is recording against, or null when it is not.</summary>
    public string? TargetBaseUrl(TenantId tenant)
    {
        lock (_gate)
        {
            return _byTenant.TryGetValue(tenant, out var state) ? state.Target : null;
        }
    }

    /// <summary>Begins recording for a tenant against a target, discarding that tenant's prior capture.</summary>
    public void Start(TenantId tenant, string targetBaseUrl)
    {
        lock (_gate)
        {
            // Only this tenant's captures are discarded. A team starting a recording must not throw
            // away another team's, which is exactly what a single global session did.
            var state = StateFor(tenant);
            state.Target = targetBaseUrl;
            state.Exchanges.Clear();
        }
    }

    /// <summary>Records one proxied exchange (the response already decoded for stub generation).</summary>
    public void Record(TenantId tenant, CanonicalRequest request, CanonicalResponse response)
    {
        // Two captures repeat each other when method, URL, and request body all match — the same
        // identity the generated request pattern matches on, so chained stubs stay distinguishable.
        var key = $"{request.Method} {request.Url}\n{Convert.ToBase64String(request.Body)}";
        lock (_gate)
        {
            StateFor(tenant).Exchanges.Add(new Exchange(key, request, response));
        }
    }

    /// <summary>
    /// The exchanges captured so far, as they happened (#287). Used by drift detection to ask whether
    /// the stubs already authored would have answered the way the real upstream just did.
    /// </summary>
    /// <remarks>
    /// Deliberately the raw exchanges rather than the generated stubs: the generated form has already
    /// been shaped for authoring (deduplicated, chained into scenarios), and a drift report needs what
    /// the upstream actually returned.
    /// </remarks>
    public IReadOnlyList<(CanonicalRequest Request, CanonicalResponse Response)> Captured(TenantId tenant)
    {
        lock (_gate)
        {
            return _byTenant.TryGetValue(tenant, out var state)
                ? [.. state.Exchanges.Select(e => (e.Request, e.Response))]
                : [];
        }
    }

    /// <summary>Returns the stubs generated from this tenant's captures so far, without stopping.</summary>
    public IReadOnlyList<string> Snapshot(TenantId tenant)
    {
        lock (_gate)
        {
            return Generate(tenant);
        }
    }

    /// <summary>Ends this tenant's recording and returns its generated stubs.</summary>
    public IReadOnlyList<string> Stop(TenantId tenant)
    {
        lock (_gate)
        {
            var captured = Generate(tenant);
            if (_byTenant.TryGetValue(tenant, out var state))
            {
                state.Target = null;
                state.Exchanges.Clear();
            }

            return captured;
        }
    }

    private TenantState StateFor(TenantId tenant)
    {
        if (!_byTenant.TryGetValue(tenant, out var state))
        {
            _byTenant[tenant] = state = new TenantState();
        }

        return state;
    }

    private List<string> Generate(TenantId tenant)
    {
        if (!_byTenant.TryGetValue(tenant, out var state))
        {
            return [];
        }

        var exchanges = state.Exchanges;
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var exchange in exchanges)
        {
            totals[exchange.Key] = totals.GetValueOrDefault(exchange.Key) + 1;
        }

        var stubs = new List<string>(exchanges.Count);
        var scenarioNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var exchange in exchanges)
        {
            if (totals[exchange.Key] == 1)
            {
                stubs.Add(RecordingJsonWriter.ToStubJson(exchange.Request, exchange.Response));
                continue;
            }

            if (!scenarioNames.TryGetValue(exchange.Key, out var scenario))
            {
                scenario = $"scenario-{scenarioNames.Count + 1}-{Slug(exchange.Request.Url)}";
                scenarioNames[exchange.Key] = scenario;
            }

            var position = positions.GetValueOrDefault(exchange.Key) + 1;
            positions[exchange.Key] = position;

            stubs.Add(RecordingJsonWriter.ToStubJson(
                exchange.Request, exchange.Response,
                scenario,
                requiredScenarioState: position == 1 ? "Started" : $"{scenario}-{position}",
                newScenarioState: position == totals[exchange.Key] ? null : $"{scenario}-{position + 1}"));
        }

        return stubs;
    }

    /// <summary>The oracle's scenario-name shape: the URL reduced to lowercase alphanumerics and dashes.</summary>
    private static string Slug(string url)
    {
        var slug = new StringBuilder(url.Length);
        foreach (var c in url)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(char.ToLowerInvariant(c));
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().TrimEnd('-');
    }
}
