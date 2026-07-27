using Mockifyr.Core;

namespace Mockifyr.Stores.InMemory;

/// <summary>
/// Host-wide in-memory API key store (G19d). Hydrated from <see cref="IApiKeyPersistence"/> at
/// startup by the host; every mutation is mirrored to the persistence by the handlers. Thread-safe.
/// </summary>
public sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly Dictionary<string, ApiKey> _byId = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public void Put(ApiKey key)
    {
        lock (_gate)
        {
            _byId[key.Id] = key;
        }
    }

    /// <inheritdoc />
    public ApiKey? Get(string id)
    {
        lock (_gate)
        {
            return _byId.GetValueOrDefault(id);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ApiKey> GetKeys(TenantId tenant)
    {
        lock (_gate)
        {
            return [.. _byId.Values.Where(k => k.Tenant == tenant).OrderByDescending(k => k.CreatedAt)];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ApiKey> GetAll()
    {
        lock (_gate)
        {
            return [.. _byId.Values];
        }
    }

    /// <inheritdoc />
    public bool Remove(string id)
    {
        lock (_gate)
        {
            return _byId.Remove(id);
        }
    }
}
