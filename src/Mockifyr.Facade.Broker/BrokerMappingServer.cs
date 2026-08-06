using Mockifyr.Core;
using Mockifyr.Templating;

namespace Mockifyr.Facade.Broker;

/// <summary>One message a matched mapping decided to emit, with every template already rendered.</summary>
/// <param name="Topic">Where it goes.</param>
/// <param name="Key">Its partition key, or null.</param>
/// <param name="Body">Its payload, or null for a tombstone.</param>
/// <param name="Headers">Its headers.</param>
public sealed record PlannedMessage(
    string Topic,
    string? Key,
    string? Body,
    IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>
/// Decides what an inbound broker message produces (ADR 0013, slice 3): which mappings match, and what
/// they emit once their templates are rendered.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and deliberately separate from the consumer loop — planning is where every interesting
/// decision lives (which stub won, what the templates rendered to, what happens when one of them
/// throws), and none of it should need a broker in the room to assert.
/// </para>
/// <para>
/// <b>Every</b> matching mapping contributes, not just the first. A broker fan-out is a real pattern —
/// one command producing an event and an audit record from two separate stubs — and first-match-wins
/// would make expressing it impossible without merging unrelated mappings. This is the one place the
/// broker channel deliberately departs from HTTP serving, where exactly one response can be sent.
/// </para>
/// </remarks>
public sealed class BrokerMappingPlanner(BrokerMappingStore store, MessageTemplateRenderer renderer)
{
    /// <summary>What <paramref name="record"/> produces for <paramref name="tenant"/>, in mapping order.</summary>
    /// <remarks>
    /// A message matching nothing produces nothing, and that is not an error: ADR 0013 says an
    /// unmatched message is captured and acknowledged rather than parked, because a mock that stalls a
    /// partition over a forgotten stub is a worse failure than an unmatched request's 404.
    /// </remarks>
    public IReadOnlyList<PlannedMessage> Plan(TenantId tenant, ConsumedRecord record)
    {
        var planned = new List<PlannedMessage>();

        foreach (var mapping in store.For(tenant))
        {
            if (!mapping.Matches(record))
            {
                continue;
            }

            foreach (var publish in mapping.Publishes)
            {
                var message = Model(record);
                try
                {
                    planned.Add(new PlannedMessage(
                        renderer.Render(publish.Topic, message, tenant),
                        publish.Key is null ? null : renderer.Render(publish.Key, message, tenant),
                        publish.Body is null ? null : renderer.Render(publish.Body, message, tenant),
                        [.. publish.Headers.Select(header => new KeyValuePair<string, string>(
                            header.Key, renderer.Render(header.Value, message, tenant)))]));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One broken template must not silence the other mappings that matched the same
                    // message. Skipping it here is what keeps a typo in an audit stub from also
                    // stopping the event the system under test is actually waiting for.
                    Failures.Add(new PlanFailure(publish.Topic, exception.Message));
                }
            }
        }

        return planned;
    }

    /// <summary>
    /// Templates that failed to render, newest last. Bounded, and read by the host for logging — a
    /// silent render failure is the same trap `publish` had before 1.10.1.
    /// </summary>
    public PlanFailureLog Failures { get; } = new();

    /// <summary>What a mapping's templates see: the message, in the vocabulary ADR 0013 named.</summary>
    private static Dictionary<string, object?> Model(ConsumedRecord record) => new()
    {
        ["body"] = record.Value ?? string.Empty,
        ["topic"] = record.Topic,
        ["key"] = record.Key ?? string.Empty,

        // Headers by name, so `{{message.headers.correlation-id}}` works. Later duplicates lose to the
        // first, matching how `TenantOf` reads the tenant header — one rule for repeated names.
        ["headers"] = record.Headers
            .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (object?)group.First().Value, StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>A template that could not be rendered, and why.</summary>
/// <param name="Topic">The destination it was meant for.</param>
/// <param name="Error">The renderer's own words.</param>
public sealed record PlanFailure(string Topic, string Error);

/// <summary>A small bounded log of render failures, so they are visible without being unbounded.</summary>
public sealed class PlanFailureLog
{
    /// <summary>How many failures are kept. A broken template repeats every message; a few is a sample.</summary>
    public const int Capacity = 32;

    private readonly Queue<PlanFailure> _failures = new();
    private readonly Lock _gate = new();

    /// <summary>Records a failure, evicting the oldest past <see cref="Capacity"/>.</summary>
    public void Add(PlanFailure failure)
    {
        lock (_gate)
        {
            _failures.Enqueue(failure);
            while (_failures.Count > Capacity)
            {
                _failures.Dequeue();
            }
        }
    }

    /// <summary>A snapshot, oldest first.</summary>
    public IReadOnlyList<PlanFailure> Snapshot()
    {
        lock (_gate)
        {
            return [.. _failures];
        }
    }
}
