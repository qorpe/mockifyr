using Mockifyr.Core;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure coverage for the backup archive format (#252): a round trip must preserve what an operator
/// authored, and the reader must refuse anything that is not an archive it understands rather than
/// letting a destructive restore proceed on a guess.
/// </summary>
public sealed class BackupJsonTests
{
    private static BackupArchive Sample() => new(
        new TenantId("acme"),
        new DateTimeOffset(2026, 7, 30, 12, 0, 0, 123, TimeSpan.FromHours(3)).AddTicks(4567),
        ["""{"request":{"method":"GET","urlPath":"/a"},"response":{"status":200,"body":"ok"}}"""],
        [new EnvironmentKey("apiHost", "staging", [
            new EnvironmentValue("staging", "https://staging.example.com"),
            new EnvironmentValue("prod", "https://example.com"),
        ])],
        [new ResourceDocument("42", "orders", """{"total":10}""", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 3)],
        [new ApiKey("key-1", new TenantId("acme"), "partner-ci", "s4lt", "h4sh", "mfk_abc123",
            new DateTimeOffset(2026, 7, 1, 9, 30, 15, 250, TimeSpan.Zero).AddTicks(89), 500)],
        new Dictionary<string, string> { ["checkout"] = "PAID" });

    [Fact]
    public void A_round_trip_preserves_every_section()
    {
        var restored = BackupJson.Read(BackupJson.Write(Sample()));

        Assert.NotNull(restored);
        Assert.Equal("acme", restored.Tenant.Value);
        // Exact, to the tick and with the offset intact: a timestamp rounded on the way through is a
        // backup whose age nobody can trust.
        Assert.Equal(new DateTimeOffset(2026, 7, 30, 12, 0, 0, 123, TimeSpan.FromHours(3)).AddTicks(4567), restored.CreatedAt);

        var mapping = Assert.Single(restored.Mappings);
        Assert.Contains("\"/a\"", mapping);

        var environment = Assert.Single(restored.Environments);
        Assert.Equal("apiHost", environment.Key);
        Assert.Equal("staging", environment.ActiveValue);
        Assert.Equal(["staging", "prod"], environment.Values.Select(v => v.Name));
        Assert.Equal("https://example.com", environment.Values[1].Value);

        var document = Assert.Single(restored.Resources);
        Assert.Equal("orders", document.Collection);
        Assert.Equal("42", document.Id);
        Assert.Contains("\"total\"", document.Body);

        var key = Assert.Single(restored.ApiKeys);
        Assert.Equal("key-1", key.Id);
        Assert.Equal("partner-ci", key.Name);
        // The verifier must survive, or every consumer's key stops working after a restore — which is
        // the one thing a restore exists to prevent.
        Assert.Equal("s4lt", key.Salt);
        Assert.Equal("h4sh", key.Hash);
        // The display prefix is how an operator recognises a key in the Access screen; losing it makes
        // every restored key anonymous.
        Assert.Equal("mfk_abc123", key.Prefix);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 9, 30, 15, 250, TimeSpan.Zero).AddTicks(89), key.CreatedAt);
        Assert.Equal(500, key.QuotaPerHour);

        Assert.Equal("PAID", restored.Scenarios["checkout"]);
    }

    [Fact]
    public void The_token_never_appears_in_an_archive()
    {
        // Only the salted verifier is stored anywhere in Mockifyr, so there is no token to leak — this
        // pins that the writer does not invent a field for one, and that no `mfk_` token value rides
        // along beside the display prefix.
        var json = BackupJson.Write(Sample());

        Assert.DoesNotContain("\"token\"", json);
        Assert.DoesNotContain("\"secret\"", json);
        Assert.Equal(1, json.Split("mfk_").Length - 1); // the display prefix, and nothing else
    }

    [Fact]
    public void A_key_without_a_quota_round_trips_as_unlimited()
    {
        var archive = Sample() with
        {
            ApiKeys = [new ApiKey("key-2", new TenantId("acme"), "open", "s", "h", "mfk_x", DateTimeOffset.UnixEpoch, null)],
        };

        var restored = BackupJson.Read(BackupJson.Write(archive));

        Assert.Null(Assert.Single(restored!.ApiKeys).QuotaPerHour);
    }

    [Fact]
    public void Empty_state_round_trips_as_empty_rather_than_missing()
    {
        var empty = new BackupArchive(
            TenantId.Default, DateTimeOffset.UnixEpoch, [], [], [], [], new Dictionary<string, string>());

        var restored = BackupJson.Read(BackupJson.Write(empty));

        Assert.NotNull(restored);
        Assert.Empty(restored.Mappings);
        Assert.Empty(restored.Environments);
        Assert.Empty(restored.Resources);
        Assert.Empty(restored.ApiKeys);
        Assert.Empty(restored.Scenarios);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("""{"mappings":[]}""")]
    [InlineData("""{"mockifyrBackup":"1"}""")]
    public void Anything_that_is_not_an_archive_is_refused(string json) =>
        // Restoring is destructive. A mapping bundle, a spec, or an unrelated file must be refused
        // outright rather than treated as an archive with everything missing — which would silently
        // wipe the tenant.
        Assert.Null(BackupJson.Read(json));

    [Fact]
    public void An_archive_from_a_newer_version_is_refused()
    {
        var future = BackupJson.Write(Sample())
            .Replace("\"mockifyrBackup\": 1", $"\"mockifyrBackup\": {BackupArchive.FormatVersion + 1}");

        // Guessing at a format we do not know is how a restore quietly drops the sections it could not
        // parse. Refusing tells the operator to upgrade first.
        Assert.Null(BackupJson.Read(future));
    }

    [Fact]
    public void Malformed_entries_are_skipped_rather_than_failing_the_whole_archive()
    {
        var json = """
        {
          "mockifyrBackup": 1,
          "tenant": "acme",
          "createdAt": "2026-07-30T12:00:00.0000000+00:00",
          "mappings": [],
          "environments": [{"activeValue":"x","values":[]}, {"key":"good","activeValue":"a","values":[{"name":"a","value":"1"}]}],
          "resources": [{"collection":"orders"}, {"collection":"orders","id":"1","body":{"ok":true}}],
          "apiKeys": [{"id":"no-hash"}, {"id":"k","salt":"s","hash":"h"}],
          "scenarios": {"checkout": "PAID"}
        }
        """;

        var restored = BackupJson.Read(json);

        // An entry missing the fields that identify it cannot be restored, but it must not take the
        // rest of the archive with it — a partial restore an operator can see beats an all-or-nothing
        // refusal over one bad row.
        Assert.NotNull(restored);
        Assert.Equal("good", Assert.Single(restored.Environments).Key);
        Assert.Equal("1", Assert.Single(restored.Resources).Id);
        Assert.Equal("k", Assert.Single(restored.ApiKeys).Id);
    }

    [Fact]
    public void An_archive_carries_the_stub_source_verbatim()
    {
        var authored = """{"request":{"method":"POST","urlPath":"/x"},"response":{"status":201},"metadata":{"team":"payments"}}""";
        var archive = Sample() with { Mappings = [authored] };

        var restored = BackupJson.Read(BackupJson.Write(archive));

        // Re-serializing a stub would quietly rewrite what the operator wrote — metadata, ordering,
        // fields Mockifyr does not model. The archive is a copy, not an interpretation.
        Assert.Contains("\"team\"", Assert.Single(restored!.Mappings));
        Assert.Contains("payments", Assert.Single(restored.Mappings));
    }
}
