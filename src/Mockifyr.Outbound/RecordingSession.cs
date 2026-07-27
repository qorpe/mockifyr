using System.Text;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;

namespace Mockifyr.Outbound;

/// <summary>
/// The state of a live recording (G12d): while active, the mock-serving facade proxies requests to
/// <see cref="TargetBaseUrl"/> and records each exchange here. Shared as a singleton between the
/// admin endpoints (start/stop/snapshot) and the mock-serving fallback. Thread-safe.
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

    private readonly Lock _gate = new();
    private readonly List<Exchange> _exchanges = [];
    private string? _target;

    /// <summary>The upstream base URL to proxy to while recording, or null when not recording.</summary>
    public string? TargetBaseUrl
    {
        get
        {
            lock (_gate)
            {
                return _target;
            }
        }
    }

    /// <summary>Begins recording against a target, discarding any prior capture.</summary>
    public void Start(string targetBaseUrl)
    {
        lock (_gate)
        {
            _target = targetBaseUrl;
            _exchanges.Clear();
        }
    }

    /// <summary>Records one proxied exchange (the response already decoded for stub generation).</summary>
    public void Record(CanonicalRequest request, CanonicalResponse response)
    {
        // Two captures repeat each other when method, URL, and request body all match — the same
        // identity the generated request pattern matches on, so chained stubs stay distinguishable.
        var key = $"{request.Method} {request.Url}\n{Convert.ToBase64String(request.Body)}";
        lock (_gate)
        {
            _exchanges.Add(new Exchange(key, request, response));
        }
    }

    /// <summary>Returns the stubs generated from the captures so far without stopping.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return Generate();
        }
    }

    /// <summary>Ends recording and returns the generated stubs.</summary>
    public IReadOnlyList<string> Stop()
    {
        lock (_gate)
        {
            var captured = Generate();
            _target = null;
            _exchanges.Clear();
            return captured;
        }
    }

    private List<string> Generate()
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var exchange in _exchanges)
        {
            totals[exchange.Key] = totals.GetValueOrDefault(exchange.Key) + 1;
        }

        var stubs = new List<string>(_exchanges.Count);
        var scenarioNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var exchange in _exchanges)
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
