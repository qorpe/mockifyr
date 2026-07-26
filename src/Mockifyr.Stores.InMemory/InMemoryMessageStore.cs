using System.Collections.Concurrent;
using Mockifyr.Core;

namespace Mockifyr.Stores.InMemory;

/// <summary>
/// Tenant-scoped in-memory message inbox (G18, ADR 0009). Bounded per tenant: beyond
/// <see cref="Capacity"/> the oldest message is evicted first, so an unattended host cannot grow
/// without limit. Newest-first reads, insertion-ordered internally.
/// </summary>
public sealed class InMemoryMessageStore(int capacity = InMemoryMessageStore.DefaultCapacity) : IMessageStore
{
    /// <summary>The default per-tenant inbox bound (matches the request journal's ethos: enough to debug, never unbounded).</summary>
    public const int DefaultCapacity = 1000;

    private readonly ConcurrentDictionary<TenantId, List<MessageEnvelope>> _byTenant = new();
    private readonly Lock _gate = new();

    /// <summary>The per-tenant bound this store was built with.</summary>
    public int Capacity { get; } = capacity > 0 ? capacity : DefaultCapacity;

    /// <inheritdoc />
    public void Append(TenantId tenant, MessageEnvelope message)
    {
        lock (_gate)
        {
            var messages = _byTenant.GetOrAdd(tenant, static _ => []);
            messages.Add(message);
            // One append can overflow by at most one, so evicting the single oldest entry is enough.
            if (messages.Count > Capacity)
            {
                messages.RemoveAt(0);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MessageEnvelope> GetMessages(TenantId tenant)
    {
        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var messages))
            {
                return [];
            }

            var snapshot = new MessageEnvelope[messages.Count];
            for (var i = 0; i < messages.Count; i++)
            {
                snapshot[i] = messages[messages.Count - 1 - i];
            }

            return snapshot;
        }
    }

    /// <inheritdoc />
    public MessageEnvelope? Get(TenantId tenant, Guid id)
    {
        lock (_gate)
        {
            return _byTenant.TryGetValue(tenant, out var messages)
                ? messages.Find(m => m.Id == id)
                : null;
        }
    }

    /// <inheritdoc />
    public bool Remove(TenantId tenant, Guid id)
    {
        lock (_gate)
        {
            return _byTenant.TryGetValue(tenant, out var messages) && messages.RemoveAll(m => m.Id == id) > 0;
        }
    }

    /// <inheritdoc />
    public void Reset(TenantId tenant)
    {
        lock (_gate)
        {
            _byTenant.TryRemove(tenant, out _);
        }
    }
}

/// <summary>The default sink: append to the store. Behavior (events, webhooks) decorates this (G18e).</summary>
public sealed class StoreMessageSink(IMessageStore store) : IMessageSink
{
    /// <inheritdoc />
    public void Accept(TenantId tenant, MessageEnvelope message) => store.Append(tenant, message);
}

/// <summary>Tenant-scoped in-memory behavior directives (G18e); absent tenants read the no-directives default.</summary>
public sealed class InMemoryMessageBehaviorStore : IMessageBehaviorStore
{
    private readonly ConcurrentDictionary<TenantId, MessageBehaviors> _byTenant = new();

    /// <inheritdoc />
    public MessageBehaviors Get(TenantId tenant) =>
        _byTenant.TryGetValue(tenant, out var behaviors) ? behaviors : MessageBehaviors.None;

    /// <inheritdoc />
    public void Set(TenantId tenant, MessageBehaviors behaviors) => _byTenant[tenant] = behaviors;

    /// <inheritdoc />
    public void Reset(TenantId tenant) => _byTenant.TryRemove(tenant, out _);
}
