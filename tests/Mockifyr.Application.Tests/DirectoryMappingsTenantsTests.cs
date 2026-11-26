using Mockifyr.Core;
using Mockifyr.Server;

namespace Mockifyr.Application.Tests;

/// <summary>
/// #467: a <c>--root-dir</c> host reloads every tenant <see cref="FileSystemStubPersistence"/> wrote, and nothing a person
/// put there. Decision (a): a subdirectory of <c>mappings/</c> is a tenant only when every entry in it is a file this
/// provider writes — <c>&lt;guid&gt;.json</c> holding one stub whose id is that guid. The class is "a directory that is
/// not one this provider wrote"; the rule is an allow-list of what the provider writes, so every member below is
/// skipped whole and every member nobody has named yet is too.
/// </summary>
public sealed class DirectoryMappingsTenantsTests : IDisposable
{
    private const string Stub = """{"request":{"method":"GET","url":"/x"},"response":{"status":200,"body":"tenant"}}""";

    private readonly string _mappings = Path.Combine(Path.GetTempPath(), $"mockifyr-tenants-{Guid.NewGuid():N}", "mappings");

    public DirectoryMappingsTenantsTests() => Directory.CreateDirectory(_mappings);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_mappings)!, recursive: true);

    [Fact]
    public void A_tenant_the_persistence_wrote_is_reloaded_with_its_ids_and_the_flat_files_stay_the_default_tenants()
    {
        var persistence = new FileSystemStubPersistence(_mappings);
        var acme = Saved(persistence, new TenantId("acme"));
        var other = Saved(persistence, new TenantId("other-team"));
        var mine = Saved(persistence, TenantId.Default);

        var loader = new DirectoryMappingsLoader(_mappings);
        var loaded = loader.LoadAllTenants();

        Assert.Equal(
            [(new TenantId("acme"), acme), (TenantId.Default, mine), (new TenantId("other-team"), other)],
            loaded.Select(stub => (stub.TenantId, stub.Id)).OrderBy(pair => pair.TenantId.Value, StringComparer.Ordinal));
        Assert.Empty(loader.SkippedDirectories);

        // Load(tenant), the single-tenant seam, is unchanged: the flat files, read into the tenant asked for.
        Assert.Equal([mine], loader.Load(TenantId.Default).Select(stub => stub.Id));
    }

    /// <summary>Every way a directory can be one a person made — each is skipped whole, and none of its stubs loads.</summary>
    [Theory]
    [InlineData("notes", "README.md", "hello")]                                    // a file that is not a stub
    [InlineData("handmade", "orders.json", Stub)]                                  // a stub, but not named <guid>.json
    [InlineData("upper", "UPPER", Stub)]                                           // <GUID>.JSON is not what it writes
    [InlineData("mismatch", "id-mismatch", Stub)]                                  // <guid>.json whose stub id is another
    [InlineData("noid", "no-id", Stub)]                                            // <guid>.json whose stub carries no id
    [InlineData("bundle", "bundle", Stub)]                                         // a {"mappings":[…]} bundle under a guid name
    [InlineData("broken", "broken", "{not json")]                                  // a file that does not parse
    [InlineData("default", "valid", Stub)]                                         // named like the default tenant
    [InlineData("nested", "subdirectory", Stub)]                                   // a directory inside it
    [InlineData(".hidden-os", ".DS_Store", "x")]                                   // an OS file beside valid stubs
    public void A_directory_this_provider_did_not_write_is_skipped_whole(string directoryName, string kind, string content)
    {
        var directory = Path.Combine(_mappings, directoryName);
        Directory.CreateDirectory(directory);
        var valid = Guid.NewGuid();
        File.WriteAllText(Path.Combine(directory, $"{valid}.json"), WithId(Stub, valid));   // one good file beside the bad one
        var other = Guid.NewGuid();
        switch (kind)
        {
            case "UPPER":
                File.WriteAllText(Path.Combine(directory, $"{other.ToString().ToUpperInvariant()}.JSON"), WithId(content, other));
                break;
            case "id-mismatch":
                File.WriteAllText(Path.Combine(directory, $"{other}.json"), WithId(content, Guid.NewGuid()));
                break;
            case "no-id":
                File.WriteAllText(Path.Combine(directory, $"{other}.json"), content);
                break;
            case "bundle":
                File.WriteAllText(Path.Combine(directory, $"{other}.json"), $$"""{"mappings":[{{WithId(content, other)}},{{content}}]}""");
                break;
            case "broken":
                File.WriteAllText(Path.Combine(directory, $"{other}.json"), content);
                break;
            case "valid":
                break;
            case "subdirectory":
                Directory.CreateDirectory(Path.Combine(directory, "inner"));
                break;
            default:
                File.WriteAllText(Path.Combine(directory, kind), content);
                break;
        }

        var loader = new DirectoryMappingsLoader(_mappings);
        var loaded = loader.LoadAllTenants();

        Assert.DoesNotContain(loaded, stub => stub.TenantId == new TenantId(directoryName));
        Assert.DoesNotContain(loaded, stub => stub.Id == valid);
        Assert.Contains(loader.SkippedDirectories, entry => entry.StartsWith(directoryName + ": ", StringComparison.Ordinal));
    }

    /// <summary>A skipped directory is logged once, not on every reload (git sync reloads after every pull).</summary>
    [Fact]
    public void A_skipped_directory_is_listed_once_across_reloads()
    {
        Directory.CreateDirectory(Path.Combine(_mappings, "notes"));
        File.WriteAllText(Path.Combine(_mappings, "notes", "README.md"), "hello");
        var loader = new DirectoryMappingsLoader(_mappings);

        loader.LoadAllTenants();
        loader.LoadAllTenants();

        Assert.Single(loader.SkippedDirectories);
    }

    /// <summary>An empty or missing mappings directory loads nothing and skips nothing.</summary>
    [Fact]
    public void A_missing_or_empty_tenant_directory_loads_nothing()
    {
        Assert.Empty(new DirectoryMappingsLoader(Path.Combine(_mappings, "absent")).LoadAllTenants());
        Directory.CreateDirectory(Path.Combine(_mappings, "empty"));
        var loader = new DirectoryMappingsLoader(_mappings);
        Assert.Empty(loader.LoadAllTenants());
        Assert.Empty(loader.SkippedDirectories);
    }

    private static Guid Saved(FileSystemStubPersistence persistence, TenantId tenant)
    {
        var stub = Adapters.MappingJson.MappingJsonReader.Read(Stub, tenant).Single();
        persistence.Save(stub, Stub);
        return stub.Id;
    }

    private static string WithId(string json, Guid id) => json.StartsWith('{') && json.Length > 1 && json[1] == '"'
        ? $$"""{"id":"{{id}}",{{json[1..]}}"""
        : json;
}
