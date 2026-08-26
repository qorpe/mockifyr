using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Mockifyr.Core;

namespace Mockifyr.Facade.Broker;

/// <summary>How this host consumes (ADR 0013, slice 2).</summary>
/// <param name="BootstrapServers">The broker to connect to.</param>
/// <param name="Topics">The topics to subscribe to. Empty means capture is off.</param>
/// <param name="GroupId">
/// The consumer group. One group per host by default, so two replicas share a subscription rather than
/// each receiving every message — which is what an operator scaling out expects.
/// </param>
public sealed record BrokerCaptureOptions(string BootstrapServers, IReadOnlyList<string> Topics, string GroupId);

/// <summary>
/// Subscribes to topics and lands what arrives in the tenant's inbox (ADR 0013, slice 2).
/// </summary>
/// <remarks>
/// <para>
/// Capture is what makes "assert my system emitted <c>OrderSettled</c>" one call against the inbox
/// people already query, rather than a bespoke consumer in every test suite.
/// </para>
/// <para>
/// <b>Offsets commit after the message is in the inbox</b>, never before. A host that crashed between
/// receiving and storing would otherwise have acknowledged a message nobody can see — at-least-once,
/// as ADR 0013 states, with redelivery preferred over silent loss.
/// </para>
/// </remarks>
public sealed class KafkaCaptureService(
    BrokerCaptureOptions options,
    IMessageSink sink,
    TimeProvider? clock = null,
    BrokerMappingPlanner? planner = null,
    IBrokerPublisher? publisher = null,
    // Last, and optional: callers construct this positionally, so a parameter inserted mid-list
    // silently rebinds their arguments rather than failing to compile (#396).
    TenantHeaderOptions? tenantHeader = null) : BackgroundService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Topics.Count == 0)
        {
            return Task.CompletedTask;
        }

        // A dedicated thread rather than the thread pool: the consumer's Consume() blocks, and parking a
        // pool thread on it for the host's lifetime is exactly the pattern that starves everything else.
        return Task.Factory.StartNew(
            () => Run(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Run(CancellationToken stoppingToken)
    {
        using var consumer = new ConsumerBuilder<string?, string?>(new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = options.GroupId,

            // Earliest, so a host started after the producer still captures what it missed — a test
            // suite that raced the broker would otherwise see an empty inbox and blame the mock.
            AutoOffsetReset = AutoOffsetReset.Earliest,

            // Committed by hand, after the inbox write.
            EnableAutoCommit = false,
        }).Build();

        consumer.Subscribe(options.Topics);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string?, string?>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                }
                catch (ConsumeException)
                {
                    // A topic that does not exist yet, a rebalance, a broker blip: none of these should
                    // end capture for the host's lifetime. Keep polling.
                    continue;
                }

                if (result?.Message is null)
                {
                    continue;
                }

                var record = new ConsumedRecord(
                    result.Topic,
                    result.Message.Key,
                    result.Message.Value,
                    result.Partition.Value,
                    result.Offset.Value,
                    [.. (result.Message.Headers ?? []).Select(h => new KeyValuePair<string, string>(
                        h.Key, System.Text.Encoding.UTF8.GetString(h.GetValueBytes() ?? [])))]);

                var tenant = BrokerMessageFactory.TenantOf(record, (tenantHeader ?? TenantHeaderOptions.Default).Name);
                sink.Accept(tenant, BrokerMessageFactory.Build(record, _clock.GetUtcNow()));

                // Serve on consume (slice 3): what the tenant's broker mappings say this message
                // produces. Captured first and served second, so a message is in the inbox — and
                // therefore assertable — even if a mapping's template or the broker itself fails.
                Serve(tenant, record, stoppingToken);

                // Only now: the message is captured and anything it produces has been dispatched, so
                // acknowledging it cannot lose either half. ADR 0013 states this ordering.
                try
                {
                    consumer.Commit(result);
                }
                catch (KafkaException)
                {
                    // A failed commit means redelivery, which at-least-once already allows for.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        finally
        {
            consumer.Close();
        }
    }

    /// <summary>Publishes whatever the tenant's broker mappings say this message produces.</summary>
    /// <remarks>
    /// Synchronous by choice: the offset must not be committed until the reply has been dispatched, and
    /// a fire-and-forget send would acknowledge a message whose reply is still in a buffer. Ordering is
    /// per partition and this consumes one message at a time, so nothing is serialised that was not
    /// already.
    /// </remarks>
    private void Serve(TenantId tenant, ConsumedRecord record, CancellationToken cancellationToken)
    {
        if (planner is null || publisher is null)
        {
            return;
        }

        foreach (var message in planner.Plan(tenant, record))
        {
            try
            {
                publisher
                    .PublishAsync(message.Topic, message.Key, message.Body, message.Headers, cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A broker that will not take the reply must not stop this host from consuming the
                // next message: the alternative is one bad topic stalling a partition, which ADR 0013
                // rules out for the same reason it does not park unmatched messages.
                planner.Failures.Add(new PlanFailure(message.Topic, exception.Message));
            }
        }
    }
}
