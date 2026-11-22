using System.Globalization;
using Mockifyr.Core;
using Mockifyr.Server;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Every LiteDB-backed component compares strings ordinally (#466, verifier on #468). LiteDB 5 opens a file with a
/// case-insensitive collation, so on a LiteDB host two tenants — or two collections, ids or environment keys — whose
/// names differ only in case were one key and one filter match. Each fact opens a file with LiteDB's own default, the
/// shape every file written before this change has, and hands it to the component as the host would: the component
/// must make it ordinal before it touches it, whoever constructed it.
/// </summary>
public sealed class LiteDbOrdinalCollationTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mockifyr-468-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string File => Path.Combine(_dir.FullName, "mockifyr.db");

    private static readonly TenantId Lower = new("acme");
    private static readonly TenantId Upper = new("ACME");

    [Fact]
    public void A_file_opened_with_the_default_collation_is_rebuilt_ordinal_by_the_host_entry_point_and_keeps_its_documents()
    {
        using (var legacy = new LiteDB.LiteDatabase(File))
        {
            Assert.NotEqual(CompareOptions.Ordinal, legacy.Collation.SortOptions);
            legacy.GetCollection("stubs").Insert(new LiteDB.BsonDocument { ["_id"] = "acme|1", ["Tenant"] = "acme" });
            legacy.GetCollection("stubs").Insert(new LiteDB.BsonDocument { ["_id"] = "ACME|2", ["Tenant"] = "ACME" });
        }

        using (var database = LiteDbCollation.Open(File))
        {
            Assert.Equal(CompareOptions.Ordinal, database.Collation.SortOptions);
            var stubs = database.GetCollection("stubs");
            Assert.Equal(2, stubs.Count());
            Assert.Equal(1, stubs.Count(LiteDB.Query.EQ("Tenant", "acme")));
            stubs.Insert(new LiteDB.BsonDocument { ["_id"] = "ACME|1", ["Tenant"] = "ACME" });
            Assert.Equal(3, stubs.Count());
        }

        // Re-runnable, and the collation is the file's own: a plain reopen is still ordinal.
        using var reopened = LiteDbCollation.Open(File);
        Assert.Equal(CompareOptions.Ordinal, reopened.Collation.SortOptions);
        Assert.Equal(3, reopened.GetCollection("stubs").Count());
    }

    public static TheoryData<Type> LiteDbComponents()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(LiteDbCollation).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => type.GetConstructors().Any(ctor => ctor.GetParameters().Any(p => p.ParameterType == typeof(LiteDB.LiteDatabase)))))
        {
            data.Add(type);
        }

        return data;
    }

    [Fact]
    public void The_sweep_finds_every_LiteDB_component_the_host_registers()
    {
        // A floor, so the sweep below cannot pass by finding nothing: the eight the host builds on --litedb.
        Assert.True(LiteDbComponents().Count >= 8, $"found {LiteDbComponents().Count}");
    }

    [Theory]
    [MemberData(nameof(LiteDbComponents))]
    public void Every_component_built_on_a_LiteDB_file_makes_it_ordinal_before_it_uses_it(Type component)
    {
        // The fail-closed rule for the class: any component that takes a LiteDatabase, including one nobody has
        // written yet, leaves its file ordinal, so no key or filter of it can compare case-insensitively.
        using var database = new LiteDB.LiteDatabase(File);
        Assert.NotEqual(CompareOptions.Ordinal, database.Collation.SortOptions);
        var ctor = component.GetConstructors().First(c => c.GetParameters().Any(p => p.ParameterType == typeof(LiteDB.LiteDatabase)));
        var arguments = ctor.GetParameters()
            .Select(p => p.ParameterType == typeof(LiteDB.LiteDatabase) ? database : p.HasDefaultValue ? p.DefaultValue : null)
            .ToArray();
        ctor.Invoke(arguments);
        Assert.Equal(CompareOptions.Ordinal, database.Collation.SortOptions);
    }

    [Fact]
    public void Stub_persistence_keeps_case_variant_tenants_apart_on_a_default_collation_file()
    {
        using var database = new LiteDB.LiteDatabase(File);
        var stubs = new LiteDbStubPersistence(database);
        var persistence = new Persisting(stubs);
        var loader = new LiteDbMappingsLoader(database);
        var id = Guid.Parse("6f1c1b7e-2d3a-4b5c-9e8f-0a1b2c3d4e5f");

        persistence.Save(Lower, Stub(id, "acme"));
        persistence.Save(Upper, Stub(id, "ACME"));
        Assert.Equal("acme", Body(Assert.Single(loader.Load(Lower))));
        Assert.Equal("ACME", Body(Assert.Single(loader.Load(Upper))));

        // A reset of one case variant (P7) and a delete (P5) leave the other alone.
        stubs.Clear(Upper);
        Assert.Single(loader.Load(Lower));
        persistence.Save(Upper, Stub(id, "ACME"));
        stubs.Remove(Upper, id);
        Assert.Single(loader.Load(Lower));
        Assert.Empty(loader.Load(Upper));
    }

    [Fact]
    public void Resource_persistence_keeps_case_variant_tenants_collections_and_ids_apart()
    {
        using var database = new LiteDB.LiteDatabase(File);
        var persistence = new LiteDbResourcePersistence(database);
        var loader = new LiteDbResourcesLoader(database);

        persistence.Save(Lower, Resource("o-1", "orders"));
        persistence.Save(Upper, Resource("o-1", "orders"));
        persistence.Save(Lower, Resource("O-1", "orders"));
        persistence.Save(Lower, Resource("o-1", "Orders"));
        Assert.Equal(3, loader.LoadAll()[Lower].Count);

        persistence.Clear(Upper, null);
        Assert.Equal(3, loader.LoadAll()[Lower].Count);
        persistence.Clear(Lower, "Orders");
        persistence.Remove(Lower, "orders", "O-1");
        Assert.Equal("o-1", Assert.Single(loader.LoadAll()[Lower]).Id);
        Assert.Equal("orders", Assert.Single(loader.LoadAll()[Lower]).Collection);
    }

    [Fact]
    public void Environment_persistence_keeps_case_variant_tenants_and_keys_apart()
    {
        using var database = new LiteDB.LiteDatabase(File);
        var persistence = new LiteDbEnvironmentPersistence(database);
        var loader = new LiteDbEnvironmentsLoader(database);

        persistence.Save(Lower, Key("apiToken"));
        persistence.Save(Lower, Key("APITOKEN"));
        persistence.Save(Upper, Key("apiToken"));
        Assert.Equal(2, loader.LoadAll()[Lower].Count);

        persistence.Clear(Upper);
        persistence.Remove(Lower, "APITOKEN");
        Assert.Equal("apiToken", Assert.Single(loader.LoadAll()[Lower]).Key);
    }

    [Fact]
    public void Tenant_persistence_keeps_case_variant_declarations_apart()
    {
        using var database = new LiteDB.LiteDatabase(File);
        var persistence = new LiteDbTenantPersistence(database);

        persistence.Save(new TenantRecord(Lower, "lower", DateTimeOffset.UnixEpoch));
        persistence.Save(new TenantRecord(Upper, "upper", DateTimeOffset.UnixEpoch));
        Assert.Equal(2, persistence.LoadAll().Count);

        persistence.Remove(Upper);
        Assert.Equal("acme", Assert.Single(persistence.LoadAll()).Id.Value);
    }

    [Fact]
    public void Under_tr_TR_the_dotted_and_dotless_i_variants_of_a_tenant_stay_four_tenants()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            using var database = new LiteDB.LiteDatabase(File);
            var stubs = new LiteDbStubPersistence(database);
            var persistence = new Persisting(stubs);
            var loader = new LiteDbMappingsLoader(database);
            var id = Guid.NewGuid();
            foreach (var name in new[] { "istanbul", "Istanbul", "İSTANBUL", "ıstanbul" })
            {
                persistence.Save(new TenantId(name), Stub(id, name));
            }

            stubs.Remove(new TenantId("Istanbul"), id);
            foreach (var name in new[] { "istanbul", "İSTANBUL", "ıstanbul" })
            {
                Assert.Equal(name, Body(Assert.Single(loader.Load(new TenantId(name)))));
            }

            Assert.Empty(loader.Load(new TenantId("Istanbul")));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    private sealed class Persisting(LiteDbStubPersistence inner)
    {
        public void Save(TenantId tenant, string json)
        {
            var stub = Assert.Single(Mockifyr.Adapters.MappingJson.MappingJsonReader.Read(json, tenant));
            inner.Save(stub, json);
        }
    }

    private static string Stub(Guid id, string body) =>
        "{\"id\":\"" + id + "\",\"request\":{\"method\":\"GET\",\"url\":\"/t\"},\"response\":{\"status\":200,\"body\":\"" + body + "\"}}";

    private static string Body(StubMapping stub) => System.Text.Encoding.UTF8.GetString(stub.Response.Body ?? []);

    private static ResourceDocument Resource(string id, string collection) =>
        new(id, collection, "{}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);

    private static EnvironmentKey Key(string key) => new(key, "dev", [new EnvironmentValue("dev", key)]);
}
