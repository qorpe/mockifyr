using Mockifyr.Core;

namespace Mockifyr.Server;

/// <summary>
/// The in-memory state a change-feed reload reconciles, with the loaders that read it back from the
/// shared backend (#279). Bundled rather than passed as six constructor arguments to every reloader,
/// so adding a fourth kind of state later is one change here instead of one per transport.
/// </summary>
public sealed class ChangeFeedTargets(
    IStubStore stubs,
    IEnumerable<IMappingsLoader> mappingsLoaders,
    IEnvironmentStore environments,
    IEnumerable<IEnvironmentsLoader> environmentsLoaders,
    IResourceStore resources,
    IEnumerable<IResourcesLoader> resourcesLoaders,
    ChangeFeedIdentity identity)
{
    /// <summary>This host's identity, so its own announcements are recognised and skipped.</summary>
    public ChangeFeedIdentity Identity { get; } = identity;

    /// <summary>
    /// Serializes this host's reloads. Both transports can deliver announcements concurrently, and two
    /// overlapping reloads read the backend at different instants — the later read can finish first and
    /// leave the host holding the older view, with nothing left to announce and correct it.
    /// </summary>
    internal Lock Gate { get; } = new();

    public IStubStore Stubs { get; } = stubs;

    public IEnumerable<IMappingsLoader> MappingsLoaders { get; } = mappingsLoaders;

    public IEnvironmentStore Environments { get; } = environments;

    public IEnumerable<IEnvironmentsLoader> EnvironmentsLoaders { get; } = environmentsLoaders;

    public IResourceStore Resources { get; } = resources;

    public IEnumerable<IResourcesLoader> ResourcesLoaders { get; } = resourcesLoaders;
}

/// <summary>
/// The reconcile step shared by the change-feed reloaders (G16e/G16f): on a change announced by another
/// instance, reload what is persisted and bring this host's in-memory state into line — upserting what's
/// persisted, then pruning what's gone — so a change made elsewhere takes effect here without a restart.
/// </summary>
/// <remarks>
/// <para>
/// Reconciliation spans <em>every</em> tenant (G16g): a mappings loader that implements
/// <see cref="IMultiTenantMappingsLoader"/> contributes all tenants' stubs, others contribute the default
/// tenant; the environment and resource loaders are multi-tenant by contract. Every tenant present in the
/// reload <em>or</em> currently in memory is reconciled, so a tenant whose last entry was deleted
/// elsewhere is pruned too. Upsert precedes prune per tenant so there is no empty window in which a live
/// request could miss an existing match.
/// </para>
/// <para>
/// All three kinds of state reload together (#279). Before that only stubs did: an operator who changed
/// an environment key's active value saw one replica honour it and another keep serving the old value
/// until it restarted — a split that reads as non-deterministic traffic rather than as a stale cache. One
/// announcement reloads all three, because a change to any of them is rare next to the request traffic it
/// affects and three channels would buy precision nobody is measuring.
/// </para>
/// </remarks>
internal static class ChangeFeedReconciler
{
    /// <summary>
    /// Handles an announcement: skips this host's own, and otherwise reconciles under the gate.
    /// </summary>
    public static void Reload(ChangeFeedTargets targets, string? announcedBy)
    {
        if (ChangeFeedAnnouncement.IsOwn(announcedBy, targets.Identity))
        {
            // This host wrote it, so memory is already ahead of what a reload would read back. Reloading
            // anyway is not merely wasted work: the read can predate the write that announced it, and
            // restoring it would serve an operator their own change back at the previous version.
            return;
        }

        Reload(targets);
    }

    /// <summary>Reconciles every kind of state, whatever announced it.</summary>
    public static void Reload(ChangeFeedTargets targets)
    {
        lock (targets.Gate)
        {
            ReloadStubs(targets.Stubs, targets.MappingsLoaders);
            ReloadEnvironments(targets.Environments, targets.EnvironmentsLoaders);
            ReloadResources(targets.Resources, targets.ResourcesLoaders);
        }
    }

    /// <summary>
    /// Reconciles stubs alone. Git sync (ADR 0007) uses this after a pull: the remote tree is mapping
    /// files, so a pull carries no opinion about environment keys or sandbox documents and must not be
    /// read as one — reconciling them against it would prune state the remote never described.
    /// </summary>
    public static void ReloadStubs(IStubStore store, IEnumerable<IMappingsLoader> loaders)
    {
        // Collect the persisted stubs across all tenants: multi-tenant loaders enumerate every tenant;
        // single-tenant loaders (e.g. a mappings directory) contribute the default tenant only.
        var loaded = new List<StubMapping>();
        foreach (var loader in loaders)
        {
            loaded.AddRange(loader is IMultiTenantMappingsLoader multi
                ? multi.LoadAllTenants()
                : loader.Load(TenantId.Default));
        }

        var loadedByTenant = loaded.GroupBy(stub => stub.TenantId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<StubMapping>)[.. group]);

        foreach (var tenant in TenantsToReconcile(loadedByTenant.Keys, store.GetTenants()))
        {
            var tenantStubs = loadedByTenant.GetValueOrDefault(tenant, []);
            var loadedIds = tenantStubs.Select(stub => stub.Id).ToHashSet();

            foreach (var stub in tenantStubs)
            {
                store.Put(stub);
            }

            foreach (var existing in store.GetStubs(tenant).ToList())
            {
                if (!loadedIds.Contains(existing.Id))
                {
                    store.Remove(tenant, existing.Id);
                }
            }
        }
    }

    private static void ReloadEnvironments(IEnvironmentStore store, IEnumerable<IEnvironmentsLoader> loaders)
    {
        // A host with no environment persistence registers no loader; reconciling against "nothing
        // persisted" would then delete the keys held in memory, so an absent loader means no opinion.
        var loaded = Merge(loaders.Select(loader => loader.LoadAll()));
        if (loaded is null)
        {
            return;
        }

        foreach (var tenant in TenantsToReconcile(loaded.Keys, store.GetTenants()))
        {
            var keys = loaded.GetValueOrDefault(tenant, []);
            var loadedNames = keys.Select(key => key.Key).ToHashSet(StringComparer.Ordinal);

            foreach (var key in keys)
            {
                store.Put(tenant, key);
            }

            foreach (var existing in store.GetKeys(tenant).ToList())
            {
                if (!loadedNames.Contains(existing.Key))
                {
                    store.Remove(tenant, existing.Key);
                }
            }
        }
    }

    private static void ReloadResources(IResourceStore store, IEnumerable<IResourcesLoader> loaders)
    {
        var loaded = Merge(loaders.Select(loader => loader.LoadAll()));
        if (loaded is null)
        {
            return;
        }

        foreach (var tenant in TenantsToReconcile(loaded.Keys, store.GetTenants()))
        {
            var documents = loaded.GetValueOrDefault(tenant, []);

            foreach (var document in documents)
            {
                // Restore, not Put: the version and timestamps are the other instance's, and a client
                // reading the same document from two replicas must not see them disagree.
                store.Restore(tenant, document);
            }

            // Prune per collection — the identity of a document is (collection, id), and the same id may
            // legitimately exist in two collections of one tenant.
            var loadedIds = documents
                .GroupBy(document => document.Collection, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(document => document.Id).ToHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal);

            foreach (var collection in store.GetCollections(tenant))
            {
                var keep = loadedIds.GetValueOrDefault(collection.Name);
                foreach (var existing in store.List(tenant, collection.Name))
                {
                    if (keep is null || !keep.Contains(existing.Id))
                    {
                        store.Delete(tenant, collection.Name, existing.Id);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Every tenant in the reload plus every tenant currently in memory — the latter so a tenant emptied
    /// elsewhere is pruned here rather than left stale.
    /// </summary>
    private static HashSet<TenantId> TenantsToReconcile(
        IEnumerable<TenantId> loaded, IEnumerable<TenantId> inMemory)
    {
        var tenants = new HashSet<TenantId>(loaded);
        tenants.UnionWith(inMemory);
        return tenants;
    }

    /// <summary>
    /// Flattens what several loaders read back, or null when there are no loaders at all — "no persistence
    /// configured" and "persistence configured and empty" must not be confused, since the second is a
    /// mandate to prune and the first is not.
    /// </summary>
    private static Dictionary<TenantId, IReadOnlyList<T>>? Merge<T>(
        IEnumerable<IReadOnlyDictionary<TenantId, IReadOnlyList<T>>> results)
    {
        Dictionary<TenantId, List<T>>? merged = null;
        foreach (var result in results)
        {
            merged ??= [];
            foreach (var (tenant, items) in result)
            {
                if (!merged.TryGetValue(tenant, out var list))
                {
                    merged[tenant] = list = [];
                }

                list.AddRange(items);
            }
        }

        return merged?.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<T>)pair.Value);
    }
}
