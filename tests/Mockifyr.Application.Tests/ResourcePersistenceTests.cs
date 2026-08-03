using System.Text.Json;
using Mockifyr.Core;
using Mockifyr.Server;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure coverage for file-backed resource persistence. Document ids and collection names are chosen by
/// whoever seeds the sandbox, so most of this is about names that are not safe path segments — they
/// must neither escape the directory nor lose the document.
/// </summary>
public sealed class ResourcePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mockifyr-resfs-{Guid.NewGuid():N}");

    private FileSystemResourcePersistence Persistence() => new(_root);

    private FileSystemResourcesLoader Loader() => new(_root);

    private static ResourceDocument Doc(string collection, string id, string body = """{"a":1}""") =>
        new(id, collection, body, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_saved_document_loads_back_with_its_fields_intact()
    {
        Persistence().Save(TenantId.Default, Doc("orders", "A-1", """{"total":10}"""));

        var loaded = Assert.Single(Loader().LoadAll()[TenantId.Default]);

        Assert.Equal("A-1", loaded.Id);
        Assert.Equal("orders", loaded.Collection);
        Assert.Equal("""{"total":10}""", loaded.Body);
    }

    [Fact]
    public void A_replaced_document_does_not_duplicate()
    {
        var persistence = Persistence();
        persistence.Save(TenantId.Default, Doc("orders", "A-1", """{"v":1}"""));
        persistence.Save(TenantId.Default, Doc("orders", "A-1", """{"v":2}"""));

        var loaded = Assert.Single(Loader().LoadAll()[TenantId.Default]);
        Assert.Equal("""{"v":2}""", loaded.Body);
    }

    [Fact]
    public void Documents_load_back_under_the_tenant_that_owns_them()
    {
        Persistence().Save(new TenantId("alpha"), Doc("orders", "shared"));
        Persistence().Save(new TenantId("beta"), Doc("orders", "shared"));

        var all = Loader().LoadAll();

        // The same id in two tenants. Keyed on collection+id alone, one would overwrite the other —
        // and only after a restart, which is the hardest kind of bug to believe.
        Assert.Single(all[new TenantId("alpha")]);
        Assert.Single(all[new TenantId("beta")]);
    }

    [Fact]
    public void Removing_a_document_removes_only_that_one()
    {
        var persistence = Persistence();
        persistence.Save(TenantId.Default, Doc("orders", "A-1"));
        persistence.Save(TenantId.Default, Doc("orders", "A-2"));

        persistence.Remove(TenantId.Default, "orders", "A-1");

        Assert.Equal("A-2", Assert.Single(Loader().LoadAll()[TenantId.Default]).Id);
    }

    [Fact]
    public void Removing_an_absent_document_is_not_an_error()
    {
        Persistence().Remove(TenantId.Default, "orders", "never-existed");
        Assert.Empty(Loader().LoadAll());
    }

    [Fact]
    public void Clearing_a_collection_leaves_the_others()
    {
        var persistence = Persistence();
        persistence.Save(TenantId.Default, Doc("keep", "k-1"));
        persistence.Save(TenantId.Default, Doc("drop", "d-1"));

        persistence.Clear(TenantId.Default, "drop");

        Assert.Equal("k-1", Assert.Single(Loader().LoadAll()[TenantId.Default]).Id);
    }

    [Fact]
    public void Clearing_a_tenant_leaves_the_other_tenants()
    {
        var persistence = Persistence();
        persistence.Save(new TenantId("alpha"), Doc("orders", "a-1"));
        persistence.Save(new TenantId("beta"), Doc("orders", "b-1"));

        persistence.Clear(new TenantId("alpha"), collection: null);

        var all = Loader().LoadAll();
        Assert.False(all.ContainsKey(new TenantId("alpha")));
        Assert.Single(all[new TenantId("beta")]);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a\\b")]
    [InlineData("with space")]
    [InlineData("üñïçø∂é")]
    public void An_id_that_is_not_a_safe_file_name_round_trips_without_escaping_the_store(string id)
    {
        Persistence().Save(TenantId.Default, Doc("orders", id));

        var loaded = Assert.Single(Loader().LoadAll()[TenantId.Default]);

        // The id survives exactly — it is read from the document, not from the file name — and every
        // file written sits under the root.
        Assert.Equal(id, loaded.Id);
        Assert.All(
            Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories),
            file => Assert.StartsWith(_root, Path.GetFullPath(file), StringComparison.Ordinal));
    }

    [Fact]
    public void A_collection_that_is_not_a_safe_file_name_round_trips_too()
    {
        Persistence().Save(TenantId.Default, Doc("../evil", "A-1"));

        Assert.Equal("../evil", Assert.Single(Loader().LoadAll()[TenantId.Default]).Collection);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "evil", "A-1.json")));
    }

    [Fact]
    public void Two_ids_that_escape_to_the_same_characters_stay_separate()
    {
        var persistence = Persistence();
        persistence.Save(TenantId.Default, Doc("orders", "a/b"));
        persistence.Save(TenantId.Default, Doc("orders", "a%2Fb"));

        // Percent-escaping must be injective, or one document silently replaces the other: "a/b"
        // becomes "a%2Fb", so a literal "a%2Fb" has to escape further ("a%252Fb").
        Assert.Equal(2, Loader().LoadAll()[TenantId.Default].Count);
    }

    [Fact]
    public void A_missing_directory_loads_as_empty() => Assert.Empty(Loader().LoadAll());

    [Fact]
    public void A_file_that_is_not_a_document_is_skipped_rather_than_failing_the_load()
    {
        Persistence().Save(TenantId.Default, Doc("orders", "A-1"));
        var tenantDirectory = Directory.EnumerateDirectories(_root).Single();
        File.WriteAllText(Path.Combine(tenantDirectory, "hand-edited.json"), "{ not json");

        // Someone will open this directory. One bad file must not take the rest of the sandbox with it.
        Assert.Single(Loader().LoadAll()[TenantId.Default]);
    }
}
