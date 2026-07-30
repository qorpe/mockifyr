using System.Security.Cryptography;
using System.Text;
using Mockifyr.Core;
using Mockifyr.Crypto;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for request signature verification and response signing (G20c, ADR 0012). The
/// signatures here are computed independently in the test, so these are checks against the scheme
/// rather than against the implementation's own arithmetic.
/// </summary>
public sealed class HmacSigningTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly SignatureRequirement Requirement =
        new(HmacSignatureVerifier.SchemeName, "X-JWS-Signature", "Digest");

    private static string Digest(string body) =>
        "SHA-256=" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private static string Signature(string digest, byte[]? key = null) =>
        Convert.ToBase64String(HMACSHA256.HashData(key ?? Key, Encoding.UTF8.GetBytes(digest)));

    private static CanonicalRequest Request(string body, params (string Name, string Value)[] headers) =>
        CanonicalRequestBuilder.Build(
            "POST", "/pay",
            [.. headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value))],
            Encoding.UTF8.GetBytes(body), "https");

    [Fact]
    public void A_correctly_signed_request_verifies()
    {
        const string body = """{"amount":10}""";
        var digest = Digest(body);

        Assert.True(new HmacSignatureVerifier(Key).Verify(
            Request(body, ("Digest", digest), ("X-JWS-Signature", Signature(digest))), Requirement));
    }

    [Fact]
    public void A_tampered_body_fails_even_though_the_signature_itself_is_valid()
    {
        // The classic attack this guards: a valid signature over a digest that no longer describes
        // the body. Checking only the signature would let it through.
        var digest = Digest("""{"amount":10}""");

        Assert.False(new HmacSignatureVerifier(Key).Verify(
            Request("""{"amount":9999}""", ("Digest", digest), ("X-JWS-Signature", Signature(digest))), Requirement));
    }

    [Fact]
    public void A_matching_digest_without_a_valid_signature_fails()
    {
        // And the reverse: an honest digest of the real body proves nothing on its own.
        const string body = """{"amount":10}""";
        var digest = Digest(body);
        var verifier = new HmacSignatureVerifier(Key);

        Assert.False(verifier.Verify(Request(body, ("Digest", digest)), Requirement));
        Assert.False(verifier.Verify(
            Request(body, ("Digest", digest), ("X-JWS-Signature", "not-a-signature")), Requirement));
        Assert.False(verifier.Verify(
            Request(body, ("Digest", digest), ("X-JWS-Signature", Signature(digest, RandomNumberGenerator.GetBytes(32)))),
            Requirement));
    }

    [Fact]
    public void Missing_headers_and_unknown_schemes_fail_rather_than_throw()
    {
        var verifier = new HmacSignatureVerifier(Key);

        Assert.False(verifier.Verify(Request("""{"a":1}"""), Requirement));
        Assert.True(verifier.Handles("hmac-sha256"));
        Assert.True(verifier.Handles("HMAC-SHA256"));
        Assert.False(verifier.Handles("rsa-sha256"));
    }

    [Fact]
    public void The_gate_fails_closed_when_nothing_can_verify_the_scheme()
    {
        const string body = """{"a":1}""";
        var digest = Digest(body);
        var signed = Request(body, ("Digest", digest), ("X-JWS-Signature", Signature(digest)));

        // No requirement declared → nothing to check.
        Assert.True(new SignatureGate([]).Satisfied(signed, null));

        // Requirement declared but no verifier registered → MUST fail: a host that cannot check a
        // signature must not accept one, or the stub's guarantee is fiction.
        Assert.False(new SignatureGate([]).Satisfied(signed, Requirement));

        // Registered and handling → the real answer.
        var gate = new SignatureGate([new HmacSignatureVerifier(Key)]);
        Assert.True(gate.Satisfied(signed, Requirement));
        Assert.False(gate.Satisfied(Request(body), Requirement));

        // Registered but a different scheme → still closed.
        Assert.False(gate.Satisfied(signed, Requirement with { Scheme = "rsa-sha256" }));
    }

    [Fact]
    public void Signing_adds_a_digest_of_the_served_body_and_its_signature()
    {
        const string body = """{"ok":true}""";
        var response = new CanonicalResponse
        {
            Status = 200,
            Headers = Array.Empty<KeyValuePair<string, string>>().ToLookup(x => x.Key, x => x.Value),
            Body = Encoding.UTF8.GetBytes(body),
        };

        var signed = new HmacResponseSigner(Key).Sign(
            response, new ResponseSignature(HmacResponseSigner.SchemeName, "X-JWS-Signature", "Digest"));

        var digest = signed.Headers["Digest"].Single();
        Assert.Equal(Digest(body), digest);
        Assert.Equal(Signature(digest), signed.Headers["X-JWS-Signature"].Single());
        // The body is untouched — signing describes it, it does not change it.
        Assert.Same(response.Body, signed.Body);
    }

    [Fact]
    public void A_hardcoded_stale_digest_header_is_replaced_not_duplicated()
    {
        const string body = """{"ok":true}""";
        var response = new CanonicalResponse
        {
            Status = 200,
            Headers = new[]
            {
                new KeyValuePair<string, string>("Digest", "SHA-256=stale"),
                new KeyValuePair<string, string>("X-Trace", "keep-me"),
            }.ToLookup(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
            Body = Encoding.UTF8.GetBytes(body),
        };

        var signed = new HmacResponseSigner(Key).Sign(
            response, new ResponseSignature(HmacResponseSigner.SchemeName, "X-JWS-Signature", "Digest"));

        // A stale digest is worse than none: a verifying client would reject the response outright.
        Assert.Equal(Digest(body), signed.Headers["Digest"].Single());
        Assert.Equal("keep-me", signed.Headers["X-Trace"].Single());
    }

    [Fact]
    public void The_signing_applier_only_acts_on_a_declared_and_handled_scheme()
    {
        var response = new CanonicalResponse
        {
            Status = 200,
            Headers = Array.Empty<KeyValuePair<string, string>>().ToLookup(x => x.Key, x => x.Value),
            Body = Encoding.UTF8.GetBytes("{}"),
        };
        var applier = new ResponseSigningApplier([new HmacResponseSigner(Key)]);

        Assert.Same(response, applier.For(response, null));
        Assert.Same(response, applier.For(response, new ResponseSignature("rsa-sha256", "X-Sig", "Digest")));
        Assert.NotSame(response, applier.For(response, new ResponseSignature("hmac-sha256", "X-Sig", "Digest")));
        Assert.Same(response, new ResponseSigningApplier([]).For(response, new ResponseSignature("hmac-sha256", "X-Sig", "Digest")));
    }
}
