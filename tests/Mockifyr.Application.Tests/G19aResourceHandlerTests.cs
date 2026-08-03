using Mockifyr.Core;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Handler-level coverage for the resource management path (G19a): the validation boundaries the
/// wire tests only sample (exact length/size limits), seed transactionality with generated ids,
/// and the pagination clamp. Handlers are constructed directly with a small body cap so boundary
/// cases stay cheap; the same rules back the REST surface verbatim.
/// </summary>
public sealed class G19aResourceHandlerTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly ResourceOptions SmallCap = new(MaxBodyBytes: 64);

    private static InMemoryResourceStore Store() => new();

    private static async Task<Mediant.Results.Result<ResourceDocument>> PutAsync(
        InMemoryResourceStore store, string collection, string id, string body)
    {
        var result = await new PutResourceHandler(store, SmallCap, new NullResourcePersistence())
            .Handle(new PutResourceCommand(collection, id, body, Acme), CancellationToken.None);
        if (result.IsFailure)
        {
            // The error contract includes a human explanation — a bare code is not an answer.
            Assert.False(string.IsNullOrEmpty(result.Error.Description));
        }

        return result;
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("_private")]
    [InlineData("a-b_c9")]
    public async Task Wellformed_collection_names_are_accepted(string collection)
    {
        var result = await PutAsync(Store(), collection, "id-1", "{}");
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("9starts-with-digit")]
    [InlineData("has space")]
    [InlineData("dotted.name")]
    [InlineData("")]
    public async Task Malformed_collection_names_are_refused(string collection)
    {
        var result = await PutAsync(Store(), collection, "id-1", "{}");
        Assert.Equal("Resource.InvalidCollection", result.Error.Code);
    }

    [Fact]
    public async Task Collection_name_length_boundary_is_exactly_64()
    {
        Assert.True((await PutAsync(Store(), "a" + new string('x', 63), "id", "{}")).IsSuccess);
        Assert.Equal("Resource.InvalidCollection",
            (await PutAsync(Store(), "a" + new string('x', 64), "id", "{}")).Error.Code);
    }

    [Fact]
    public async Task Id_boundaries_are_exactly_1_and_256_with_no_control_characters()
    {
        var store = Store();
        Assert.True((await PutAsync(store, "orders", new string('i', 256), "{}")).IsSuccess);
        Assert.Equal("Resource.InvalidId", (await PutAsync(store, "orders", new string('i', 257), "{}")).Error.Code);
        Assert.Equal("Resource.InvalidId", (await PutAsync(store, "orders", "", "{}")).Error.Code);
        Assert.Equal("Resource.InvalidId", (await PutAsync(store, "orders", "tab\tid", "{}")).Error.Code);
    }

    [Fact]
    public async Task Body_cap_boundary_is_exact_and_counts_utf8_bytes()
    {
        // 64-byte cap: {"p":"…"} carries 8 bytes of syntax, so 56 ASCII padding bytes sit AT the cap.
        var atCap = $$"""{"p":"{{new string('x', 56)}}"}""";
        Assert.True((await PutAsync(Store(), "orders", "ok", atCap)).IsSuccess);

        var overCap = $$"""{"p":"{{new string('x', 57)}}"}""";
        Assert.Equal("Resource.BodyTooLarge", (await PutAsync(Store(), "orders", "big", overCap)).Error.Code);

        // Multi-byte characters count as bytes, not chars: 19 four-byte emoji push 8+76 past 64.
        var emoji = $$"""{"p":"{{string.Concat(Enumerable.Repeat("🙂", 19))}}"}""";
        Assert.Equal("Resource.BodyTooLarge", (await PutAsync(Store(), "orders", "emoji", emoji)).Error.Code);
    }

    [Fact]
    public async Task Invalid_json_bodies_are_refused()
    {
        Assert.Equal("Resource.InvalidBody", (await PutAsync(Store(), "orders", "bad", "{not json")).Error.Code);
        Assert.Equal("Resource.InvalidBody", (await PutAsync(Store(), "orders", "empty", "")).Error.Code);
    }

    [Fact]
    public async Task Seed_generates_ids_through_the_seam_and_is_transactional()
    {
        var store = Store();
        var handler = new SeedResourcesHandler(store, new SequentialIds(), SmallCap, new NullResourcePersistence());

        var seeded = await handler.Handle(new SeedResourcesCommand("orders",
            [new SeedResourceItem(null, """{"n":1}"""), new SeedResourceItem("explicit", """{"n":2}""")], Acme),
            CancellationToken.None);
        Assert.True(seeded.IsSuccess);
        Assert.Equal(2, seeded.Value);
        Assert.NotNull(store.Get(Acme, "orders", "orders-1"));
        Assert.NotNull(store.Get(Acme, "orders", "explicit"));

        // A bad SECOND item rolls back the whole request: the valid first item never lands.
        var invalid = await handler.Handle(new SeedResourcesCommand("orders",
            [new SeedResourceItem("fresh", """{"ok":true}"""), new SeedResourceItem("bad", "{nope")], Acme),
            CancellationToken.None);
        Assert.Equal("Resource.InvalidBody", invalid.Error.Code);
        Assert.Null(store.Get(Acme, "orders", "fresh"));
        Assert.Equal(2, store.List(Acme, "orders").Count);
    }

    [Fact]
    public async Task Listing_clamps_limit_and_offset_defensively()
    {
        var store = Store();
        for (var i = 0; i < 5; i++)
        {
            store.Put(Acme, "orders", $"d{i}", "{}");
        }

        var handler = new ListResourcesHandler(store);
        var oversized = await handler.Handle(new ListResourcesQuery("orders", Limit: 9999, Offset: -5, Acme), CancellationToken.None);
        Assert.Equal(5, oversized.Value.Documents.Count);
        Assert.Equal(5, oversized.Value.Total);

        var floor = await handler.Handle(new ListResourcesQuery("orders", Limit: 0, Offset: null, Acme), CancellationToken.None);
        Assert.Single(floor.Value.Documents);

        var past = await handler.Handle(new ListResourcesQuery("orders", Limit: null, Offset: 99, Acme), CancellationToken.None);
        Assert.Empty(past.Value.Documents);
        Assert.Equal(5, past.Value.Total);
    }

    private sealed class SequentialIds : IResourceIdGenerator
    {
        private int _next;

        public string NextId(string collection) => $"{collection}-{++_next}";
    }
}
