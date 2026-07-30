using System.Diagnostics;
using System.Diagnostics.Metrics;
using Mockifyr.Core;

namespace Mockifyr.Server;

/// <summary>
/// The telemetry surface (#246): one <see cref="ActivitySource"/> and one <see cref="Meter"/>, named
/// once so a dashboard, an alert rule and a scrape config can all reference the same strings.
/// </summary>
public static class MockifyrTelemetry
{
    /// <summary>The instrumentation name traces and metrics are published under.</summary>
    public const string Name = "Mockifyr";

    /// <summary>Traces emitted by Mockifyr itself (ASP.NET and HttpClient spans come from their own sources).</summary>
    public static readonly ActivitySource Activity = new(Name);

    /// <summary>Metrics emitted by Mockifyr itself.</summary>
    public static readonly Meter Meter = new(Name);
}

/// <summary>
/// Records serving metrics from the <see cref="IServeEventListener"/> seam (#246). Deliberately a
/// listener rather than instrumentation inside <see cref="StubEngine"/>: the engine stays pure and
/// dependency-free, and every serve event already flows through this seam — the same choke point the
/// journal and webhooks use, so nothing can be served without being counted.
/// </summary>
public sealed class MetricsServeEventListener : IServeEventListener
{
    // Instrument names follow the OTel HTTP conventions where one applies, and a mockifyr.* prefix
    // where the concept is ours. Renaming these breaks dashboards, so they are contract, not detail.
    private static readonly Counter<long> Served =
        MockifyrTelemetry.Meter.CreateCounter<long>(
            "mockifyr.requests.served", "{request}", "Requests resolved by the engine.");

    private static readonly Histogram<double> ResponseStatus =
        MockifyrTelemetry.Meter.CreateHistogram<double>(
            "mockifyr.response.status", "{status}", "Served HTTP status codes.");

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

        Served.Add(1, tags);
        if (serveEvent.Response is { } response)
        {
            ResponseStatus.Record(response.Status, tags);
        }

        return Task.CompletedTask;
    }
}
