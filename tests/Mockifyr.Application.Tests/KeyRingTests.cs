using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Mockifyr.Core;
using Mockifyr.Crypto;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure coverage for key rings and key sources (#250): what counts as a key, which key is used for
/// what, and the behaviour that makes rotation safe — a file that changes is picked up, and a file
/// that is momentarily unreadable or half-written never disarms the host.
/// </summary>
public sealed class KeyRingTests : IDisposable
{
    private readonly List<string> _files = [];

    private static string Key(byte fill) => Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray());

    private string WriteFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mockifyr-keys-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, contents);
        _files.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var file in _files.Where(File.Exists))
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_ring_uses_the_newest_key_to_produce_and_accepts_them_all()
    {
        var ring = KeyRing.Parse($"new: {Key(1)}\nold: {Key(2)}");

        Assert.Equal(2, ring.Keys.Count);
        // Newest first: the file's order is the operator's statement of which key is current.
        Assert.Equal("new", ring.Primary!.Id);
        Assert.Equal(["new", "old"], ring.Keys.Select(k => k.Id));
        Assert.False(ring.IsEmpty);
    }

    [Fact]
    public void An_unnamed_key_parses_without_an_id()
    {
        var ring = KeyRing.Parse(Key(3));

        Assert.Null(Assert.Single(ring.Keys).Id);
    }

    [Fact]
    public void Comments_blank_lines_and_unusable_lines_are_skipped()
    {
        var ring = KeyRing.Parse($"""
            # the key we are rotating to
            current: {Key(4)}

            not-a-key: hello
            {Key(5)}
            short: {Convert.ToBase64String(new byte[16])}
            """);

        // A bad line can never become a usable key — it does not parse to 32 bytes — and refusing
        // the whole file over one typo would turn an editing slip during a rollover into an outage.
        Assert.Equal(2, ring.Keys.Count);
        Assert.Equal("current", ring.Keys[0].Id);
        Assert.Null(ring.Keys[1].Id);
    }

    [Fact]
    public void An_id_may_contain_colons_because_the_last_one_separates()
    {
        // Secret managers hand out namespaced ids. Splitting on the last colon keeps them intact,
        // and is unambiguous because base64 never contains one.
        var ring = KeyRing.Parse($"vault:prod:2026: {Key(6)}");

        Assert.Equal("vault:prod:2026", Assert.Single(ring.Keys).Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# only a comment")]
    [InlineData("not base64 at all")]
    public void A_file_with_no_usable_key_produces_an_empty_ring(string text) =>
        Assert.True(KeyRing.Parse(text).IsEmpty);

    [Fact]
    public void Base64url_is_accepted_as_well_as_base64()
    {
        var raw = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();
        var url = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(raw, KeyRing.ReadKey(url));
        Assert.Equal(raw, KeyRing.ReadKey(Convert.ToBase64String(raw)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zzzz")]
    public void Only_a_256_bit_key_is_a_key(string? value) => Assert.Null(KeyRing.ReadKey(value));

    [Fact]
    public void A_key_of_the_wrong_length_is_refused()
    {
        Assert.Null(KeyRing.ReadKey(Convert.ToBase64String(new byte[16])));
        Assert.Null(KeyRing.ReadKey(Convert.ToBase64String(new byte[64])));
    }

    [Fact]
    public void An_empty_ring_has_no_primary()
    {
        Assert.Null(KeyRing.Empty.Primary);
        Assert.True(KeyRing.Empty.IsEmpty);
    }

    [Fact]
    public void A_commented_out_key_is_not_active()
    {
        var ring = KeyRing.Parse($"live: {Key(1)}\n# retired: {Key(2)}");

        // Commenting a line out is how an operator retires a key without deleting it. If a `#` line
        // still parsed, the key they believe they withdrew would go on decrypting traffic — the
        // worst possible outcome for a control whose entire purpose is withdrawing trust.
        Assert.Equal("live", Assert.Single(ring.Keys).Id);
    }

    [Fact]
    public void A_stray_leading_colon_still_yields_the_key()
    {
        var ring = KeyRing.Parse($": {Key(1)}");

        // The line parses to a perfectly good key; dropping it over a typo would silently leave the
        // host without the key the operator put there.
        Assert.Null(Assert.Single(ring.Keys).Id);
    }

    [Fact]
    public void The_configured_reload_interval_is_honoured_rather_than_the_default()
    {
        var time = new FakeTimeProvider();
        var path = WriteFile($"old: {Key(1)}");
        var source = new FileKeySource(path, TimeSpan.FromSeconds(1), time);

        File.WriteAllText(path, $"new: {Key(2)}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        time.Advance(TimeSpan.FromSeconds(2));

        // Two seconds is far below the 10-second default: a deployment that asked for a tighter
        // cadence must actually get one, or a rotation window computed from it would be wrong.
        Assert.Equal("new", source.Current.Primary!.Id);
    }

    [Fact]
    public void The_reload_happens_once_the_interval_has_elapsed_exactly()
    {
        var time = new FakeTimeProvider();
        var path = WriteFile($"old: {Key(1)}");
        var source = new FileKeySource(path, TimeSpan.FromSeconds(5), time);

        File.WriteAllText(path, $"new: {Key(2)}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        time.Advance(TimeSpan.FromSeconds(5));

        // Exactly at the boundary counts as elapsed. Off by one interval is a rotation that lands a
        // poll later than the operator was told it would.
        Assert.Equal("new", source.Current.Primary!.Id);
    }

    [Fact]
    public void A_file_source_picks_up_a_rotation_without_a_restart()
    {
        var time = new FakeTimeProvider();
        var path = WriteFile($"old: {Key(1)}");
        var source = new FileKeySource(path, TimeSpan.FromSeconds(10), time);

        Assert.Equal("old", source.Current.Primary!.Id);

        File.WriteAllText(path, $"new: {Key(2)}\nold: {Key(1)}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

        // Before the interval elapses the cached ring is served — a busy host must not stat the file
        // on every request.
        Assert.Equal("old", source.Current.Primary!.Id);

        time.Advance(TimeSpan.FromSeconds(11));

        // After it, the new key is primary and the old one is still accepted: rotation is "add now,
        // remove later", never a coordinated restart.
        Assert.Equal("new", source.Current.Primary!.Id);
        Assert.Equal(["new", "old"], source.Current.Keys.Select(k => k.Id));
    }

    [Fact]
    public void An_unchanged_file_is_not_re_parsed_on_every_poll()
    {
        var time = new FakeTimeProvider();
        var source = new FileKeySource(WriteFile($"live: {Key(1)}"), TimeSpan.FromSeconds(1), time);

        var first = source.Current;
        time.Advance(TimeSpan.FromSeconds(5));
        var second = source.Current;

        // Same instance: the poll saw an unchanged timestamp and stopped. Re-reading and re-parsing
        // a file that has not changed is pure waste on a host serving thousands of requests.
        Assert.Same(first, second);
    }

    [Fact]
    public void A_value_that_decodes_to_the_wrong_length_is_refused_whatever_its_padding()
    {
        // 31 bytes base64url-encodes to a length needing two padding characters — a different
        // branch of the padding logic from a 32-byte key, and still not a key.
        var thirtyOne = Convert.ToBase64String(new byte[31]).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Null(KeyRing.ReadKey(thirtyOne));
        Assert.NotNull(KeyRing.ReadKey(Key(1)));
    }

    [Fact]
    public void A_file_that_becomes_unreadable_keeps_the_last_good_ring()
    {
        var time = new FakeTimeProvider();
        var path = WriteFile($"live: {Key(1)}");
        var source = new FileKeySource(path, TimeSpan.FromSeconds(1), time);

        File.Delete(path);
        time.Advance(TimeSpan.FromSeconds(5));

        // A rotation script that deletes and rewrites must not leave the host unable to decrypt
        // traffic that is still arriving in the gap.
        Assert.Equal("live", source.Current.Primary!.Id);
    }

    [Fact]
    public void A_momentarily_empty_file_keeps_the_last_good_ring()
    {
        var time = new FakeTimeProvider();
        var path = WriteFile($"live: {Key(1)}");
        var source = new FileKeySource(path, TimeSpan.FromSeconds(1), time);

        File.WriteAllText(path, "");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        time.Advance(TimeSpan.FromSeconds(5));

        // Truncate-then-write is how plenty of tools update a file. Reading it in between must not
        // disarm the host.
        Assert.Equal("live", source.Current.Primary!.Id);
    }

    [Fact]
    public void A_static_source_never_changes()
    {
        var source = new StaticKeySource(KeyRing.ReadKey(Key(9))!);

        Assert.Single(source.Current.Keys);
        Assert.Null(source.Current.Primary!.Id);
        Assert.Same(source.Current, source.Current);
    }

    [Fact]
    public void Rotation_lets_an_old_token_decrypt_while_new_ones_use_the_new_key()
    {
        var time = new FakeTimeProvider();
        var path = WriteFile($"old: {Key(1)}");
        var source = new FileKeySource(path, TimeSpan.FromSeconds(1), time);

        var protector = new JweResponseProtector(source);
        var decryptor = new JweFieldDecryptor(source);

        var beforeRotation = Protect(protector, """{"secret":"pre-rotation"}""");

        File.WriteAllText(path, $"new: {Key(2)}\nold: {Key(1)}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        time.Advance(TimeSpan.FromSeconds(5));

        var afterRotation = Protect(protector, """{"secret":"post-rotation"}""");

        // The whole point: a payload produced before the rollover still reads afterwards, and new
        // payloads are produced with the new key. Without both halves, rotation means downtime.
        Assert.Contains("pre-rotation", Decrypt(decryptor, beforeRotation));
        Assert.Contains("post-rotation", Decrypt(decryptor, afterRotation));
        Assert.NotEqual(beforeRotation, afterRotation);
    }

    [Fact]
    public void A_named_key_puts_its_id_in_the_token_header_and_an_unnamed_one_does_not()
    {
        var named = Protect(new JweResponseProtector(new FileKeySource(WriteFile($"prod-2026: {Key(1)}"))), """{"a":1}""");
        var unnamed = Protect(new JweResponseProtector(new StaticKeySource(KeyRing.ReadKey(Key(1))!)), """{"a":1}""");

        Assert.Contains("prod-2026", Header(named));
        // An unnamed key emits exactly the header it always did, so a host that never adopted key
        // files produces byte-identical tokens to before rotation existed.
        Assert.DoesNotContain("kid", Header(unnamed));
    }

    [Fact]
    public void A_signature_from_a_retired_key_is_still_accepted_while_it_is_in_the_ring()
    {
        var oldKey = KeyRing.ReadKey(Key(1))!;
        var source = new FileKeySource(WriteFile($"new: {Key(2)}\nold: {Key(1)}"));
        var verifier = new HmacSignatureVerifier(source);

        var body = Encoding.UTF8.GetBytes("""{"amount":100}""");
        var digest = Digest(body);
        var signedWithOld = Sign(oldKey, digest);

        var request = CanonicalRequestBuilder.Build("POST", "/pay",
            [new KeyValuePair<string, string>("Digest", digest), new KeyValuePair<string, string>("Signature", signedWithOld)],
            body);

        Assert.True(verifier.Verify(request, new SignatureRequirement(Scheme, "Signature", "Digest")));
    }

    [Fact]
    public void A_signature_from_a_key_that_left_the_ring_is_rejected()
    {
        var retired = KeyRing.ReadKey(Key(7))!;
        var verifier = new HmacSignatureVerifier(new FileKeySource(WriteFile($"only: {Key(2)}")));

        var body = Encoding.UTF8.GetBytes("""{"amount":100}""");
        var digest = Digest(body);
        var request = CanonicalRequestBuilder.Build("POST", "/pay",
            [
                new KeyValuePair<string, string>("Digest", digest),
                new KeyValuePair<string, string>("Signature", Sign(retired, digest)),
            ],
            body);

        // Removing a key from the file is what actually retires it — otherwise "rotation" would only
        // ever add trust and never withdraw it.
        Assert.False(verifier.Verify(request, new SignatureRequirement(Scheme, "Signature", "Digest")));
    }

    // Computed here rather than through the implementation's own helpers: a test that reuses the
    // code under test only proves it agrees with itself.
    private const string Scheme = "hmac-sha256";

    private static string Digest(byte[] body) => "SHA-256=" + Convert.ToBase64String(SHA256.HashData(body));

    private static string Sign(byte[] key, string digest) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(digest)));

    private static string Protect(JweResponseProtector protector, string body)
    {
        var response = new CanonicalResponse
        {
            Status = 200,
            Body = Encoding.UTF8.GetBytes(body),
            Headers = Array.Empty<KeyValuePair<string, string>>().ToLookup(x => x.Key, x => x.Value),
        };
        var protectedResponse = protector.Protect(response, new PayloadProtectDirective(JweResponseProtector.SchemeName, []));
        return Encoding.UTF8.GetString(protectedResponse.Body);
    }

    private static string Decrypt(JweFieldDecryptor decryptor, string token)
    {
        var request = CanonicalRequestBuilder.Build("POST", "/x", [], Encoding.UTF8.GetBytes(token));
        return Encoding.UTF8.GetString(
            decryptor.Decrypt(request, new PayloadDecryptDirective(JweFieldDecryptor.SchemeName, [])).Body);
    }

    private static string Header(string token) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(Pad(token.Split('.')[0])));

    private static string Pad(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        return normalized + (normalized.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
    }
}
