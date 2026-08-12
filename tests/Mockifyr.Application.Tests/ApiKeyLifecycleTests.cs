using Mockifyr.Application;
using Mockifyr.Core;
using Mockifyr.Server;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// The life of a sandbox key (#355): expiry, revocation as a state, rotation overlap and scope.
/// </summary>
public sealed class ApiKeyLifecycleTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

    private static (IApiKeyStore Store, IApiKeyPersistence Persistence) Fresh() =>
        (new InMemoryApiKeyStore(), new NullApiKeyPersistence());

    private static ApiKey Sample(
        DateTimeOffset? expiresAt = null,
        ApiKeyRevocation? revocation = null,
        ApiKeyScope scope = ApiKeyScope.ReadWrite) =>
        new("id", Acme, "partner", "salt", "hash", "mfk_abcd", Now, 100, expiresAt, revocation, scope);

    [Fact]
    public void A_key_with_no_expiry_stays_active()
    {
        Assert.Equal(ApiKeyStatus.Active, Sample().StatusAt(Now.AddYears(5)));
    }

    [Fact]
    public void Expiry_is_inclusive_of_the_instant_itself()
    {
        // A key whose expiry has just been reached is expired: "valid until 10:00" must not mean
        // "including 10:00 exactly", or the boundary is a coin toss for whoever is calling then.
        var key = Sample(expiresAt: Now.AddHours(1));

        Assert.Equal(ApiKeyStatus.Active, key.StatusAt(Now.AddMinutes(59)));
        Assert.Equal(ApiKeyStatus.Expired, key.StatusAt(Now.AddHours(1)));
    }

    [Fact]
    public void Revocation_outranks_expiry_in_the_reported_reason()
    {
        // Both are true for a key revoked and then left to lapse; the decision somebody made is the
        // more useful thing to report, and it is the one an auditor asks about.
        var key = Sample(expiresAt: Now, revocation: new ApiKeyRevocation(Now, "tenant:acme"));

        Assert.Equal(ApiKeyStatus.Revoked, key.StatusAt(Now.AddDays(1)));
    }

    [Fact]
    public async Task Issuing_refuses_an_expiry_that_has_already_passed()
    {
        var (store, persistence) = Fresh();
        var handler = new IssueApiKeyHandler(store, persistence);

        var result = await handler.Handle(
            new IssueApiKeyCommand("partner", null, Acme, DateTimeOffset.UtcNow.AddMinutes(-1)), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("ApiKey.InvalidExpiry", result.Error.Code);
    }

    [Fact]
    public async Task Revoking_keeps_the_key_and_records_who_and_why()
    {
        var (store, persistence) = Fresh();
        var issued = await new IssueApiKeyHandler(store, persistence)
            .Handle(new IssueApiKeyCommand("partner", null, Acme), default);

        var revoked = await new RevokeApiKeyHandler(store, persistence)
            .Handle(new RevokeApiKeyCommand(issued.Value.Key.Id, Acme, "tenant:acme", "pilot ended"), default);

        Assert.True(revoked.IsSuccess);
        var stored = store.Get(issued.Value.Key.Id);
        Assert.NotNull(stored);
        Assert.Equal("tenant:acme", stored!.Revocation!.By);
        Assert.Equal("pilot ended", stored.Revocation.Reason);
        Assert.Equal(ApiKeyStatus.Revoked, stored.StatusAt(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Revoking_twice_keeps_the_first_decision()
    {
        var (store, persistence) = Fresh();
        var handler = new RevokeApiKeyHandler(store, persistence);
        var issued = await new IssueApiKeyHandler(store, persistence)
            .Handle(new IssueApiKeyCommand("partner", null, Acme), default);

        await handler.Handle(new RevokeApiKeyCommand(issued.Value.Key.Id, Acme, "tenant:acme", "first"), default);
        var second = await handler.Handle(
            new RevokeApiKeyCommand(issued.Value.Key.Id, Acme, "system", "second"), default);

        Assert.True(second.IsSuccess);
        Assert.Equal("first", store.Get(issued.Value.Key.Id)!.Revocation!.Reason);
    }

    [Fact]
    public async Task One_tenant_cannot_revoke_another_tenants_key()
    {
        var (store, persistence) = Fresh();
        var issued = await new IssueApiKeyHandler(store, persistence)
            .Handle(new IssueApiKeyCommand("partner", null, Acme), default);

        var result = await new RevokeApiKeyHandler(store, persistence)
            .Handle(new RevokeApiKeyCommand(issued.Value.Key.Id, new TenantId("other")), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("ApiKey.NotFound", result.Error.Code);
        Assert.Null(store.Get(issued.Value.Key.Id)!.Revocation);
    }

    [Fact]
    public async Task Rotation_leaves_both_keys_usable_during_the_overlap()
    {
        // The point of the whole feature: a partner deploys the new credential before the old stops.
        var (store, persistence) = Fresh();
        var issued = await new IssueApiKeyHandler(store, persistence)
            .Handle(new IssueApiKeyCommand("partner", 500, Acme, null, ApiKeyScope.ReadOnly), default);

        var rotated = await new RotateApiKeyHandler(store, persistence)
            .Handle(new RotateApiKeyCommand(issued.Value.Key.Id, Acme, OverlapMinutes: 60), default);

        Assert.True(rotated.IsSuccess);
        Assert.NotEqual(issued.Value.Token, rotated.Value.Token);

        var previous = store.Get(issued.Value.Key.Id)!;
        Assert.Equal(ApiKeyStatus.Active, previous.StatusAt(DateTimeOffset.UtcNow.AddMinutes(30)));
        Assert.Equal(ApiKeyStatus.Expired, previous.StatusAt(DateTimeOffset.UtcNow.AddMinutes(61)));

        // The successor changes the secret and nothing else a partner was told about their access.
        Assert.Equal(500, rotated.Value.Key.QuotaPerHour);
        Assert.Equal(ApiKeyScope.ReadOnly, rotated.Value.Key.Scope);
        Assert.Equal("partner", rotated.Value.Key.Name);
    }

    [Fact]
    public async Task Rotation_with_no_overlap_revokes_the_predecessor_at_once()
    {
        var (store, persistence) = Fresh();
        var issued = await new IssueApiKeyHandler(store, persistence)
            .Handle(new IssueApiKeyCommand("partner", null, Acme), default);

        await new RotateApiKeyHandler(store, persistence)
            .Handle(new RotateApiKeyCommand(issued.Value.Key.Id, Acme, OverlapMinutes: 0, By: "system"), default);

        var previous = store.Get(issued.Value.Key.Id)!;
        Assert.Equal("rotated", previous.Revocation!.Reason);
        Assert.Equal("system", previous.Revocation.By);
    }

    [Fact]
    public async Task An_overlap_never_extends_a_key_that_was_expiring_sooner()
    {
        // Rotating must not resurrect a credential already on its way out.
        var (store, persistence) = Fresh();
        var soon = DateTimeOffset.UtcNow.AddMinutes(5);
        var issued = await new IssueApiKeyHandler(store, persistence)
            .Handle(new IssueApiKeyCommand("partner", null, Acme, soon), default);

        await new RotateApiKeyHandler(store, persistence)
            .Handle(new RotateApiKeyCommand(issued.Value.Key.Id, Acme, OverlapMinutes: 600), default);

        Assert.Equal(soon, store.Get(issued.Value.Key.Id)!.ExpiresAt);
    }

    [Fact]
    public async Task A_revoked_key_cannot_be_rotated()
    {
        var (store, persistence) = Fresh();
        var issued = await new IssueApiKeyHandler(store, persistence)
            .Handle(new IssueApiKeyCommand("partner", null, Acme), default);
        await new RevokeApiKeyHandler(store, persistence)
            .Handle(new RevokeApiKeyCommand(issued.Value.Key.Id, Acme), default);

        var result = await new RotateApiKeyHandler(store, persistence)
            .Handle(new RotateApiKeyCommand(issued.Value.Key.Id, Acme, 60), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("ApiKey.Revoked", result.Error.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(RotateApiKeyHandler.MaxOverlapMinutes + 1)]
    public async Task An_overlap_outside_the_bound_is_refused(int overlap)
    {
        // An unbounded overlap is not an overlap; it is two live credentials nobody is tracking.
        var (store, persistence) = Fresh();
        var issued = await new IssueApiKeyHandler(store, persistence)
            .Handle(new IssueApiKeyCommand("partner", null, Acme), default);

        var result = await new RotateApiKeyHandler(store, persistence)
            .Handle(new RotateApiKeyCommand(issued.Value.Key.Id, Acme, overlap), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("ApiKey.InvalidOverlap", result.Error.Code);
    }

    [Fact]
    public async Task Rotating_an_unknown_key_is_the_same_404_as_a_cross_tenant_one()
    {
        var (store, persistence) = Fresh();

        var result = await new RotateApiKeyHandler(store, persistence)
            .Handle(new RotateApiKeyCommand("nope", Acme, 60), default);

        Assert.Equal("ApiKey.NotFound", result.Error.Code);
    }

    [Fact]
    public void The_lifecycle_fields_round_trip_through_stored_json()
    {
        // One JSON shape serves all four providers (the G17 pattern), so proving it here proves it for
        // file, LiteDB, Postgres and Redis at once.
        var directory = Path.Combine(Path.GetTempPath(), "mockifyr-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var key = Sample(
                expiresAt: Now.AddDays(90),
                revocation: new ApiKeyRevocation(Now, "tenant:acme", "pilot ended"),
                scope: ApiKeyScope.ReadOnly);
            new FileSystemApiKeyPersistence(directory).Save(key);

            var loaded = Assert.Single(new FileSystemApiKeyPersistence(directory).LoadAll());

            Assert.Equal(key.ExpiresAt, loaded.ExpiresAt);
            Assert.Equal(key.Revocation, loaded.Revocation);
            Assert.Equal(ApiKeyScope.ReadOnly, loaded.Scope);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_row_written_before_the_lifecycle_existed_reads_back_as_it_meant()
    {
        // The compatibility claim, tested against the literal old shape rather than against a record
        // this version wrote: a key stored by an older host has none of these fields, and it was a
        // never-expiring, unrevoked, read-write key.
        var directory = Path.Combine(Path.GetTempPath(), "mockifyr-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "old.json"),
                """
                {"Id":"old","Tenant":"acme","Name":"legacy","Salt":"s","Hash":"h","Prefix":"mfk_abcd","CreatedAt":"2026-01-01T00:00:00+00:00","QuotaPerHour":null}
                """);

            var loaded = Assert.Single(new FileSystemApiKeyPersistence(directory).LoadAll());

            Assert.Null(loaded.ExpiresAt);
            Assert.Null(loaded.Revocation);
            Assert.Equal(ApiKeyScope.ReadWrite, loaded.Scope);
            Assert.Equal(ApiKeyStatus.Active, loaded.StatusAt(DateTimeOffset.UtcNow));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
