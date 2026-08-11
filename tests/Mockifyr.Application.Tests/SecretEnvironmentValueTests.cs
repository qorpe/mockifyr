using Mockifyr.Core;

namespace Mockifyr.Application.Tests;

/// <summary>
/// The read-modify-write rule behind secret environment values (#348). Redacting on read is the easy
/// half; not destroying the secret when the redacted read is handed back is the half that bites.
/// </summary>
public sealed class SecretEnvironmentValueTests
{
    private static EnvironmentKey Stored => new("apiToken", "prod",
    [
        new EnvironmentValue("prod", "s3cret-live", Secret: true),
        new EnvironmentValue("dev", "plain-dev"),
    ]);

    [Fact]
    public void A_withheld_secret_means_unchanged()
    {
        // Exactly what the dashboard sends back after a redacted read: the marker, no literal.
        var submitted = new EnvironmentKey("apiToken", "prod",
        [
            new EnvironmentValue("prod", string.Empty, Secret: true),
            new EnvironmentValue("dev", "plain-dev"),
        ]);

        var merged = EnvironmentSecrets.Merge(submitted, Stored);

        // Asserted as the value we want rather than as "not empty": an assertion phrased against the
        // bad outcome passes when the value is missing entirely, which is the bug.
        Assert.Equal("s3cret-live", merged.Values.Single(v => v.Name == "prod").Value);
        Assert.True(merged.Values.Single(v => v.Name == "prod").Secret);
    }

    [Fact]
    public void An_explicit_literal_still_replaces_it_so_rotation_works()
    {
        var submitted = new EnvironmentKey("apiToken", "prod",
            [new EnvironmentValue("prod", "s3cret-rotated", Secret: true)]);

        Assert.Equal("s3cret-rotated", EnvironmentSecrets.Merge(submitted, Stored).Values.Single().Value);
    }

    [Fact]
    public void A_brand_new_secret_with_no_literal_is_dropped_rather_than_stored_empty()
    {
        // An empty secret is a stub that signs with nothing and reports success — the failure that
        // looks like it worked.
        var submitted = new EnvironmentKey("apiToken", "prod",
        [
            new EnvironmentValue("prod", string.Empty, Secret: true),
            new EnvironmentValue("staging", string.Empty, Secret: true),
        ]);

        var merged = EnvironmentSecrets.Merge(submitted, Stored);

        Assert.Equal(["prod"], merged.Values.Select(v => v.Name));
    }

    [Fact]
    public void A_stored_secret_that_holds_nothing_carries_nothing_forward()
    {
        // Not hypothetical: restoring a redacted bundle stores exactly this — a secret marker with no
        // literal, because the export refused to carry one. Treating it as something to preserve would
        // turn "we could not restore this" into a key that resolves to the empty string, which a stub
        // would sign with and report success.
        var hollow = new EnvironmentKey("apiToken", "prod",
            [new EnvironmentValue("prod", string.Empty, Secret: true)]);
        var submitted = new EnvironmentKey("apiToken", "prod",
            [new EnvironmentValue("prod", string.Empty, Secret: true)]);

        Assert.Empty(EnvironmentSecrets.Merge(submitted, hollow).Values);
    }

    [Fact]
    public void A_stored_value_that_is_not_secret_is_never_borrowed_by_a_secret_of_the_same_name()
    {
        // The literal has to come from a value that was itself secret. Borrowing a public literal would
        // promote it to a secret nobody chose to hide, and quietly change what the key resolves to.
        var wasPublic = new EnvironmentKey("apiToken", "prod",
            [new EnvironmentValue("prod", "used-to-be-public")]);
        var submitted = new EnvironmentKey("apiToken", "prod",
            [new EnvironmentValue("prod", string.Empty, Secret: true)]);

        Assert.Empty(EnvironmentSecrets.Merge(submitted, wasPublic).Values);
    }

    [Fact]
    public void A_value_that_stops_being_secret_takes_the_submitted_literal()
    {
        var submitted = new EnvironmentKey("apiToken", "prod",
            [new EnvironmentValue("prod", "now-public", Secret: false)]);

        var merged = EnvironmentSecrets.Merge(submitted, Stored);

        Assert.Equal("now-public", merged.Values.Single().Value);
        Assert.False(merged.Values.Single().Secret);
    }

    [Fact]
    public void Creating_a_key_with_no_stored_counterpart_keeps_the_literals_it_was_given()
    {
        var submitted = new EnvironmentKey("fresh", "only",
            [new EnvironmentValue("only", "given", Secret: true)]);

        Assert.Equal("given", EnvironmentSecrets.Merge(submitted, stored: null).Values.Single().Value);
    }

    [Fact]
    public void A_deleted_value_stays_deleted_rather_than_returning_from_storage()
    {
        // The merge carries literals forward, never entries: a value the submission dropped is gone.
        var submitted = new EnvironmentKey("apiToken", "dev", [new EnvironmentValue("dev", "plain-dev")]);

        Assert.Equal(["dev"], EnvironmentSecrets.Merge(submitted, Stored).Values.Select(v => v.Name));
    }

    [Fact]
    public void The_key_still_reports_which_of_its_values_is_in_effect()
    {
        Assert.True(Stored.ResolvesToSecret());
        Assert.False((Stored with { ActiveValue = "dev" }).ResolvesToSecret());

        // A resolve of an unknown active value is neither a secret nor a literal, and must not read as
        // "secret" by accident — that would hide a plain value for no reason.
        Assert.False((Stored with { ActiveValue = "gone" }).ResolvesToSecret());
    }

    [Fact]
    public void Serve_time_resolution_is_untouched_because_a_secret_nobody_can_use_is_not_a_feature()
    {
        Assert.Equal("s3cret-live", Stored.Resolve());
    }
}
