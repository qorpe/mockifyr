using System.Diagnostics;
using System.Diagnostics.Metrics;
using Mockifyr.Core;

namespace Mockifyr.Server;

/// <summary>
/// The telemetry surface (#246): one <see cref="ActivitySource"/> and one <see cref="Meter"/>, named
/// once so a dashboard, an alert rule and a scrape config can all reference the same strings.
/// </summary>
public sealed record TelemetryOptions
{
    /// <summary>The historical instrumentation name, and the default.</summary>
    public const string DefaultName = "Mockifyr";

    /// <summary>The name traces and metrics are published under.</summary>
    public string Name { get; init; } = DefaultName;

    /// <summary>
    /// The prefix instrument names carry — the name, lowercased.
    /// </summary>
    /// <remarks>
    /// Derived rather than separately configurable, so a scrape config and an alert rule cannot be
    /// pointed at a meter that publishes under a different prefix. The default lowercases to
    /// <c>mockifyr</c>, which is exactly what shipped, so no existing dashboard moves.
    /// </remarks>
    public string InstrumentPrefix => Name.ToLowerInvariant();

    /// <summary>Nothing configured.</summary>
    public static TelemetryOptions Default { get; } = new();
}

/// <summary>
/// The telemetry surface (#246): one <see cref="ActivitySource"/> and one <see cref="Meter"/> per
/// host, named from <see cref="TelemetryOptions"/> so an operator running this under their own name
/// gets metrics under it too (#396).
/// </summary>
/// <remarks>
/// Per host rather than static: the wire tests run several hosts in one process, and a static meter
/// would publish the first host's name for all of them — the same reason the tenant header is
/// resolved from DI.
/// </remarks>
public sealed class MockifyrTelemetry(TelemetryOptions options) : IDisposable
{
    /// <summary>The instrumentation name traces and metrics are published under.</summary>
    public string Name { get; } = options.Name;

    /// <summary>Traces emitted by the host itself (ASP.NET and HttpClient spans come from their own sources).</summary>
    public ActivitySource Activity { get; } = new(options.Name);

    /// <summary>Metrics emitted by the host itself.</summary>
    public Meter Meter { get; } = new(options.Name);

    /// <inheritdoc />
    public void Dispose()
    {
        Activity.Dispose();
        Meter.Dispose();
    }
}

/// <summary>
/// Records serving metrics from the <see cref="IServeEventListener"/> seam (#246). Deliberately a
/// listener rather than instrumentation inside <see cref="StubEngine"/>: the engine stays pure and
/// dependency-free, and every serve event already flows through this seam — the same choke point the
/// journal and webhooks use, so nothing can be served without being counted.
/// </summary>
public sealed class MetricsServeEventListener : IServeEventListener
{
    // Instrument names follow the OTel HTTP conventions where one applies, and the configured prefix
    // where the concept is ours. Renaming these breaks dashboards, so the DEFAULT is contract, not
    // detail — an operator who changes it is choosing to move their own dashboards with it.
    private readonly Counter<long> _served;
    private readonly Histogram<double> _responseStatus;

    /// <summary>Creates the instruments on the host's own meter.</summary>
    public MetricsServeEventListener(MockifyrTelemetry telemetry, TelemetryOptions options)
    {
        _served = telemetry.Meter.CreateCounter<long>(
            $"{options.InstrumentPrefix}.requests.served", "{request}", "Requests resolved by the engine.");
        _responseStatus = telemetry.Meter.CreateHistogram<double>(
            $"{options.InstrumentPrefix}.response.status", "{status}", "Served HTTP status codes.");
    }

    /// <inheritdoc />
    public Task OnServeEventAsync(ServeEvent serveEvent, CancellationToken cancellationToken)
    {
        // Cardinality discipline: tenant is bounded (an operator names them), matched is a boolean,
        // and the method is a small closed set. Stub id and URL are deliberately NOT labels — a mock
        // host can hold thousands of stubs and a metrics backend would fall over.
        var tags = new TagList
        {
            { "tenant", serveEvent.TenantId.Value },
            { "matched", serveEvent.MatchedStub is not null },
            { "method", serveEvent.Request.Method },
        };

        _served.Add(1, tags);
        if (serveEvent.Response is { } response)
        {
            _responseStatus.Record(response.Status, tags);
        }

        return Task.CompletedTask;
    }
}
