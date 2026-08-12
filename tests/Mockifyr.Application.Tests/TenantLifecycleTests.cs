using Mockifyr.Application;
using Mockifyr.Core;
using Mockifyr.Server;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// A tenant as a declared object (#357): create, suspend, delete with a receipt — and the storage
/// ceiling that stops one partner filling a shared host.
/// </summary>
public sealed class TenantLifecycleTests
{
    private static readonly TenantId Acme = new("acme");

    private static DeclareTenantHandler Declare(ITenantStore store) => new(store, new NullTenantPersistence());

    [Fact]
    public async Task Declaring_a_tenant_records_a_name_and_when_it_started()
    {
        var store = new InMemoryTenantStore();

        var result = await Declare(store).Handle(new DeclareTenantCommand("acme", "Acme Ltd", null), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("Acme Ltd", result.Value.DisplayName);
        Assert.Equal(TenantStatus.Active, result.Value.Status);
        Assert.NotEqual(default, result.Value.CreatedAt);
    }

    [Fact]
    public async Task Re_declaring_keeps_the_original_date_and_does_not_resume_a_suspended_tenant()
    {
        // A rename that quietly resumed serving would be the worst kind of surprise.
        var store = new InMemoryTenantStore();
        var first = await Declare(store).Handle(new DeclareTenantCommand("acme", "Acme", null), default);
        await new SetTenantStatusHandler(store, new NullTenantPersistence())
            .Handle(new SetTenantStatusCommand("acme", TenantStatus.Suspended), default);

        var renamed = await Declare(store).Handle(new DeclareTenantCommand("acme", "Acme Ltd", 1024), default);

        Assert.Equal(first.Value.CreatedAt, renamed.Value.CreatedAt);
        Assert.Equal(TenantStatus.Suspended, renamed.Value.Status);
        Assert.Equal(1024, renamed.Value.StorageLimitBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("9starts-with-a-digit")]
    public async Task An_id_that_is_not_identifier_shaped_is_refused(string id)
    {
        var result = await Declare(new InMemoryTenantStore())
            .Handle(new DeclareTenantCommand(id, "name", null), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("Tenant.InvalidId", result.Error.Code);
    }

    [Fact]
    public async Task A_negative_storage_limit_is_refused_rather_than_read_as_unlimited()
    {
        var result = await Declare(new InMemoryTenantStore())
            .Handle(new DeclareTenantCommand("acme", "Acme", -1), default);

        Assert.Equal("Tenant.InvalidStorageLimit", result.Error.Code);
    }

    [Fact]
    public async Task Only_a_declared_tenant_can_be_suspended()
    {
        // Suspending one that was merely inferred from owning a stub would be a decision with nowhere
        // to live, lost on the next restart.
        var result = await new SetTenantStatusHandler(new InMemoryTenantStore(), new NullTenantPersistence())
            .Handle(new SetTenantStatusCommand("never-declared", TenantStatus.Suspended), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("Tenant.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task Deleting_a_tenant_removes_everything_scoped_to_it_and_says_what_went()
    {
        var tenants = new InMemoryTenantStore();
        var stubs = new InMemoryStubStore();
        var resources = new InMemoryResourceStore();
        var environments = new InMemoryEnvironmentStore();
        var keys = new InMemoryApiKeyStore();
        var messages = new InMemoryMessageStore();

        await Declare(tenants).Handle(new DeclareTenantCommand("acme", "Acme", null), default);
        var (stub, _) = Mockifyr.Adapters.MappingJson.MappingJsonReader.ReadWithSource(
            """{"request":{"method":"GET","url":"/ping"},"response":{"status":200}}""",
            Acme, new InMemoryMatcherRegistry())[0];
        stubs.Put(stub);
        resources.Put(Acme, "orders", "A-1", """{"total":1}""");
        resources.Put(Acme, "orders", "A-2", """{"total":2}""");
        environments.Put(Acme, new EnvironmentKey("greeting", "prod", [new EnvironmentValue("prod", "hi")]));
        keys.Put(new ApiKey("k", Acme, "partner", "s", "h", "mfk_abcd", DateTimeOffset.UtcNow, null));
        messages.Append(Acme, new MessageEnvelope(
            Guid.NewGuid(), MessageChannel.Sms, "from", ["to"], null, "body", null,
            new Dictionary<string, string>(), [], DateTimeOffset.UtcNow));

        // A second tenant, so the test proves scope rather than "it emptied the store".
        var other = new TenantId("other");
        resources.Put(other, "orders", "B-1", """{"total":9}""");

        var receipt = await new DeleteTenantHandler(
            tenants, new NullTenantPersistence(), stubs, new NullStubPersistence(),
            resources, new NullResourcePersistence(), environments, new NullEnvironmentPersistence(),
            keys, new NullApiKeyPersistence(), messages).Handle(new DeleteTenantCommand("acme"), default);

        Assert.Equal(1, receipt.Value.Stubs);
        Assert.Equal(2, receipt.Value.Documents);
        Assert.Equal(1, receipt.Value.EnvironmentKeys);
        Assert.Equal(1, receipt.Value.ApiKeys);
        Assert.Equal(1, receipt.Value.Messages);

        Assert.Empty(stubs.GetStubs(Acme));
        Assert.Empty(resources.GetCollections(Acme));
        Assert.Empty(environments.GetKeys(Acme));
        Assert.Empty(keys.GetKeys(Acme));
        Assert.Empty(messages.GetMessages(Acme));
        Assert.Null(tenants.Get(Acme));

        // The neighbour is untouched.
        Assert.Single(resources.List(other, "orders"));
    }

    [Fact]
    public async Task Deleting_a_tenant_nobody_declared_is_still_a_receipt_rather_than_a_404()
    {
        // Offboarding has to work for the tenants that were only ever inferred, which is all of them
        // on a host that never declared any.
        var resources = new InMemoryResourceStore();
        resources.Put(Acme, "orders", "A-1", """{"total":1}""");

        var receipt = await new DeleteTenantHandler(
            new InMemoryTenantStore(), new NullTenantPersistence(), new InMemoryStubStore(), new NullStubPersistence(),
            resources, new NullResourcePersistence(), new InMemoryEnvironmentStore(), new NullEnvironmentPersistence(),
            new InMemoryApiKeyStore(), new NullApiKeyPersistence(), new InMemoryMessageStore())
            .Handle(new DeleteTenantCommand("acme"), default);

        Assert.Equal(1, receipt.Value.Documents);
        Assert.Empty(resources.GetCollections(Acme));
    }

    // ---- the storage ceiling -------------------------------------------------------------------

    [Fact]
    public void An_unlimited_ceiling_is_what_every_host_meant_before_this_existed()
    {
        Assert.True(TenantStorage.Fits(usedBytes: 1_000_000, replacedBytes: 0, incomingBytes: 1_000_000, limit: 0));
    }

    [Fact]
    public void A_tenants_own_limit_beats_the_host_default()
    {
        var declared = new TenantRecord(Acme, "Acme", DateTimeOffset.UtcNow, StorageLimitBytes: 50);

        Assert.Equal(50, TenantStorage.LimitFor(declared, hostDefault: 500));
        Assert.Equal(500, TenantStorage.LimitFor(null, hostDefault: 500));
    }

    [Fact]
    public void Replacing_a_document_only_counts_the_difference()
    {
        // A tenant sitting at its ceiling must still be able to edit what it already has, or the limit
        // is a trap only a delete can escape.
        Assert.True(TenantStorage.Fits(usedBytes: 100, replacedBytes: 40, incomingBytes: 40, limit: 100));
        Assert.False(TenantStorage.Fits(usedBytes: 100, replacedBytes: 40, incomingBytes: 41, limit: 100));
    }

    [Fact]
    public void The_ceiling_is_inclusive_of_the_byte_that_reaches_it()
    {
        Assert.True(TenantStorage.Fits(0, 0, 100, 100));
        Assert.False(TenantStorage.Fits(0, 0, 101, 100));
    }

    [Fact]
    public void The_store_counts_what_a_tenant_holds_as_documents_come_and_go()
    {
        var store = new InMemoryResourceStore();

        store.Put(Acme, "orders", "A-1", "12345");
        store.Put(Acme, "orders", "A-2", "123");
        Assert.Equal(8, store.UsedBytes(Acme));

        store.Put(Acme, "orders", "A-1", "1");        // a replace releases the difference
        Assert.Equal(4, store.UsedBytes(Acme));

        store.Delete(Acme, "orders", "A-2");
        Assert.Equal(1, store.UsedBytes(Acme));

        store.ResetAll(Acme);
        Assert.Equal(0, store.UsedBytes(Acme));
    }

    [Fact]
    public void Resetting_one_collection_releases_only_that_collections_bytes()
    {
        var store = new InMemoryResourceStore();
        store.Put(Acme, "orders", "A-1", "12345");
        store.Put(Acme, "invoices", "B-1", "123");

        store.Reset(Acme, "orders");

        Assert.Equal(3, store.UsedBytes(Acme));
    }

    [Fact]
    public void Eviction_at_the_collection_bound_releases_the_evicted_documents_bytes()
    {
        // Otherwise the counter drifts upward forever on a busy collection and the ceiling starts
        // refusing writes for storage that is not there.
        var store = new InMemoryResourceStore(capacity: 2);

        store.Put(Acme, "orders", "A-1", "12345");
        store.Put(Acme, "orders", "A-2", "12345");
        store.Put(Acme, "orders", "A-3", "12345");

        Assert.Equal(10, store.UsedBytes(Acme));
    }

    [Fact]
    public async Task A_write_past_the_ceiling_is_refused_with_the_limit_and_the_usage()
    {
        // "You are over a limit" without either number is a support ticket, not an answer.
        var store = new InMemoryResourceStore();
        var guard = new TenantStorageGuard(store, hostDefaultBytes: 20);
        var handler = new PutResourceHandler(store, new ResourceOptions(), new NullResourcePersistence(), guard);

        var first = await handler.Handle(new PutResourceCommand("orders", "A-1", """{"a":1}""", Acme), default);
        Assert.True(first.IsSuccess);

        var refused = await handler.Handle(
            new PutResourceCommand("orders", "A-2", """{"aaaaaaaaaaaaaaaa":1}""", Acme), default);

        Assert.False(refused.IsSuccess);
        Assert.Equal("Tenant.StorageExceeded", refused.Error.Code);
        Assert.Contains("20", refused.Error.Description);
        Assert.Contains("7", refused.Error.Description);
    }

    [Fact]
    public async Task One_tenants_documents_do_not_count_against_anothers_ceiling()
    {
        var store = new InMemoryResourceStore();
        var guard = new TenantStorageGuard(store, hostDefaultBytes: 10);
        var handler = new PutResourceHandler(store, new ResourceOptions(), new NullResourcePersistence(), guard);

        await handler.Handle(new PutResourceCommand("orders", "A-1", """{"a":1}""", Acme), default);
        var neighbour = await handler.Handle(
            new PutResourceCommand("orders", "B-1", """{"a":1}""", new TenantId("other")), default);

        Assert.True(neighbour.IsSuccess);
    }

    [Fact]
    public async Task A_tenant_at_its_ceiling_can_still_edit_what_it_already_has()
    {
        // The guard has to look up what is being replaced, not just what is being written: otherwise a
        // tenant that reaches its limit can never correct a document again, only delete it.
        var store = new InMemoryResourceStore();
        var guard = new TenantStorageGuard(store, hostDefaultBytes: 9);
        var handler = new PutResourceHandler(store, new ResourceOptions(), new NullResourcePersistence(), guard);

        var seeded = await handler.Handle(new PutResourceCommand("orders", "A-1", """{"a":1}""", Acme), default);
        Assert.True(seeded.IsSuccess);
        Assert.Equal(7, store.UsedBytes(Acme));

        var edited = await handler.Handle(new PutResourceCommand("orders", "A-1", """{"a":22}""", Acme), default);

        Assert.True(edited.IsSuccess);
        Assert.Equal(8, store.UsedBytes(Acme));
    }

    [Fact]
    public void Tenants_are_listed_oldest_first_so_the_order_does_not_shuffle_between_reads()
    {
        var store = new InMemoryTenantStore();
        var start = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);
        store.Put(new TenantRecord(new TenantId("second"), "Second", start.AddHours(1)));
        store.Put(new TenantRecord(new TenantId("first"), "First", start));
        // Same instant as "second": the id breaks the tie, because two tenants declared in the same
        // second must not swap places on the operator's screen between refreshes.
        store.Put(new TenantRecord(new TenantId("aardvark"), "Aardvark", start.AddHours(1)));

        var listed = store.GetAll().Select(tenant => tenant.Id.Value).ToList();

        Assert.Equal(["first", "aardvark", "second"], listed);
    }
}
