using System.Globalization;
using Mockifyr.Core;
using Mockifyr.Server;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Every LiteDB-backed component compares strings ordinally (#466, verifier on #468). LiteDB 5 opens a file with a
/// case-insensitive collation, so on a LiteDB host two tenants — or two collections, ids or environment keys — whose
/// names differ only in case were one key and one filter match. Each component fact creates a file with LiteDB's own
/// default, the shape every file written before this change has, and opens it as the host does
/// (<see cref="LiteDbCollation.Open"/>); a component handed a case-insensitive database it did not open refuses it.
/// The rebuild carries what the connection says about the file: an encrypted file stays encrypted (verifier on #468,
/// round 3).
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
    public void Every_component_built_on_a_LiteDB_file_refuses_a_case_insensitive_one_and_leaves_it_unchanged(Type component)
    {
        // The fail-closed rule for the class: any component that takes a LiteDatabase, including one nobody has
        // written yet, refuses a file that compares case-insensitively. It does not rebuild it either: it cannot know
        // the file's password, and a rebuild without it would write an encrypted file back in plaintext.
        using var database = new LiteDB.LiteDatabase(File);
        Assert.NotEqual(CompareOptions.Ordinal, database.Collation.SortOptions);
        var refused = Assert.Throws<System.Reflection.TargetInvocationException>(() => Construct(component, database));
        Assert.IsType<InvalidOperationException>(refused.InnerException);
        Assert.Contains("LiteDbCollation.Open", refused.InnerException!.Message, StringComparison.Ordinal);
        Assert.NotEqual(CompareOptions.Ordinal, database.Collation.SortOptions);
    }

    [Theory]
    [MemberData(nameof(LiteDbComponents))]
    public void Every_component_built_on_a_LiteDB_file_accepts_the_one_the_host_entry_point_opened(Type component)
    {
        using var database = LiteDbCollation.Open(File);
        Construct(component, database);
        Assert.Equal(CompareOptions.Ordinal, database.Collation.SortOptions);
    }

    [Fact]
    public void An_encrypted_file_is_rebuilt_ordinal_and_stays_encrypted_with_its_own_password()
    {
        // The class: a rebuild that does not carry what the connection said about the file. RebuildOptions.Password
        // is the REBUILT file's password and null means none, so a rebuild without it decrypts the file.
        const string Secret = "SECRET-PAYLOAD-468";
        var connection = $"Filename={File};Password=s3cret";
        using (var legacy = new LiteDB.LiteDatabase(connection))
        {
            Assert.NotEqual(CompareOptions.Ordinal, legacy.Collation.SortOptions);
            legacy.GetCollection("stubs").Insert(new LiteDB.BsonDocument { ["_id"] = "acme|1", ["Tenant"] = "acme", ["Json"] = Secret });
        }

        Assert.False(ContainsPlaintext(Secret));

        using (var database = LiteDbCollation.Open(connection))
        {
            Assert.Equal(CompareOptions.Ordinal, database.Collation.SortOptions);
            Assert.Equal(Secret, database.GetCollection("stubs").FindById("acme|1")["Json"].AsString);
            new LiteDbStubPersistence(database);   // the host's components accept it
        }

        Assert.False(ContainsPlaintext(Secret));
        Assert.ThrowsAny<Exception>(() => new LiteDB.LiteDatabase(File).GetCollectionNames().ToList());   // no password, no file

        using var reopened = LiteDbCollation.Open(connection);
        Assert.Equal(CompareOptions.Ordinal, reopened.Collation.SortOptions);
        Assert.Equal(1, reopened.GetCollection("stubs").Count());
    }

    [Fact]
    public void A_new_encrypted_file_is_created_ordinal_and_encrypted()
    {
        const string Secret = "SECRET-NEW-468";
        var connection = $"Filename={File};Password=s3cret";
        using (var database = LiteDbCollation.Open(connection))
        {
            Assert.Equal(CompareOptions.Ordinal, database.Collation.SortOptions);
            database.GetCollection("stubs").Insert(new LiteDB.BsonDocument { ["_id"] = "a", ["Json"] = Secret });
        }

        Assert.False(ContainsPlaintext(Secret));
        using var reopened = LiteDbCollation.Open(connection);
        Assert.Equal(1, reopened.GetCollection("stubs").Count());
    }

    [Theory]
    [InlineData(":memory:")]
    [InlineData("Filename=:memory:")]
    public void A_memory_database_opens_ordinal(string connection)
    {
        using var database = LiteDbCollation.Open(connection);
        Assert.Equal(CompareOptions.Ordinal, database.Collation.SortOptions);
        var stubs = new LiteDbStubPersistence(database);
        stubs.Clear(Lower);
    }

    [Theory]
    [InlineData("")]
    [InlineData(";Collation=en-US/IgnoreCase")]
    [InlineData(";Connection=shared")]
    public void A_new_file_is_created_ordinal_whatever_collation_the_connection_string_names(string suffix)
    {
        using var database = LiteDbCollation.Open($"Filename={File}{suffix}");
        Assert.Equal(CompareOptions.Ordinal, database.Collation.SortOptions);
        var stubs = database.GetCollection("stubs");
        stubs.Insert(new LiteDB.BsonDocument { ["_id"] = "acme|1" });
        stubs.Insert(new LiteDB.BsonDocument { ["_id"] = "ACME|1" });
        Assert.Equal(2, stubs.Count());
    }

    private bool ContainsPlaintext(string text)
    {
        var bytes = System.IO.File.ReadAllBytes(File);
        return System.Text.Encoding.UTF8.GetString(bytes).Contains(text, StringComparison.Ordinal)
            || System.Text.Encoding.Unicode.GetString(bytes).Contains(text, StringComparison.Ordinal);
    }

    private static object Construct(Type component, LiteDB.LiteDatabase database)
    {
        var ctor = component.GetConstructors().First(c => c.GetParameters().Any(p => p.ParameterType == typeof(LiteDB.LiteDatabase)));
        var arguments = ctor.GetParameters()
            .Select(p => p.ParameterType == typeof(LiteDB.LiteDatabase) ? database : p.HasDefaultValue ? p.DefaultValue : null)
            .ToArray();
        return ctor.Invoke(arguments);
    }

    /// <summary>A file with LiteDB's case-insensitive default, the shape every file before #468 has, opened as the host opens it.</summary>
    private LiteDB.LiteDatabase OpenLegacy()
    {
        using (var legacy = new LiteDB.LiteDatabase(File))
        {
            Assert.NotEqual(CompareOptions.Ordinal, legacy.Collation.SortOptions);
            legacy.GetCollection("probe").Insert(new LiteDB.BsonDocument { ["_id"] = 1 });
            legacy.GetCollection("probe").Delete(1);
        }

        return LiteDbCollation.Open(File);
    }

    [Fact]
    public void Stub_persistence_keeps_case_variant_tenants_apart_on_a_default_collation_file()
    {
        using var database = OpenLegacy();
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
        using var database = OpenLegacy();
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
        using var database = OpenLegacy();
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
        using var database = OpenLegacy();
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
            using var database = OpenLegacy();
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
