using Mockifyr.Core;

namespace Mockifyr.Server;

/// <summary>
/// File-backed stub persistence (G16a): writes each stub as its own JSON mapping file so it survives a
/// restart, and <see cref="DirectoryMappingsLoader"/> reloads them on startup. It follows a
/// <c>&lt;root-dir&gt;/mappings</c> layout — the persistence directory <em>is</em> the load directory.
/// The stub's id is stamped into the saved JSON so the reloaded stub keeps the same id (the reader
/// mints a fresh id when none is present). Default-tenant stubs are written flat (matching the loader);
/// other tenants go in a per-tenant subdirectory, which <see cref="DirectoryMappingsLoader.LoadAllTenants"/> reads
/// back as that tenant because every file in it is one this provider wrote (#467).
/// File I/O lives at the host edge, never in Core.
/// </summary>
public sealed class FileSystemStubPersistence(string mappingsDirectory) : IStubPersistence
{
    /// <inheritdoc />
    public void Save(StubMapping stub, string mappingJson)
    {
        var directory = DirectoryFor(stub.TenantId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(FileFor(directory, stub.Id), PersistableJson.WithId(mappingJson, stub.Id));
    }

    /// <inheritdoc />
    public void Remove(TenantId tenant, Guid id)
    {
        var file = FileFor(DirectoryFor(tenant), id);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    /// <inheritdoc />
    public void Clear(TenantId tenant)
    {
        var directory = DirectoryFor(tenant);
        if (!Directory.Exists(directory))
        {
            return;
        }

        // Only remove the stub files this provider owns (named <guid>.json); leave other files alone.
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out _))
            {
                File.Delete(file);
            }
        }
    }

    private string DirectoryFor(TenantId tenant) =>
        tenant == TenantId.Default ? mappingsDirectory : Path.Combine(mappingsDirectory, tenant.Value);

    private static string FileFor(string directory, Guid id) =>
        Path.Combine(directory, id.ToString() + ".json");
}
