namespace Mockifyr.Core;

/// <summary>
/// A stub's declaration that named body fields arrive encrypted (G20a, ADR 0012). Opt-in per stub:
/// without it nothing about matching changes. <see cref="Scheme"/> names the wire format the
/// registered <see cref="IPayloadDecryptor"/> understands; <see cref="Fields"/> are the JSON field
/// names whose values carry ciphertext.
/// </summary>
public sealed record PayloadDecryptDirective(string Scheme, IReadOnlyList<string> Fields);

/// <summary>
/// Decrypts declared fields of a request body (G20a). Implementations live at the edge — key
/// material never enters Core, which only ever sees "a scheme was applied". The contract is a
/// <em>view</em>: the returned request is what matching and templating look through, while the
/// serve event keeps what the client actually sent, because replay, export and the differential
/// harness all depend on the recorded request being verbatim.
/// </summary>
public interface IPayloadDecryptor
{
    /// <summary>True when this decryptor handles the named scheme.</summary>
    bool Handles(string scheme);

    /// <summary>
    /// Returns a decrypted view of the request, or the request unchanged when nothing could be
    /// decrypted. Implementations must never throw on malformed input: a body that does not decrypt
    /// is a non-match, not a server error — that is what an attacker-supplied payload looks like.
    /// </summary>
    CanonicalRequest Decrypt(CanonicalRequest request, PayloadDecryptDirective directive);
}

/// <summary>
/// Applies whichever registered decryptor handles a stub's declared scheme, and caches the result
/// per (stub scheme, request) evaluation pass so a body is decrypted once even when several stubs
/// declare the same scheme. No decryptor for the scheme means no view — the stub then matches
/// against ciphertext and simply does not match, which is the honest outcome for a host that was
/// never given the key.
/// </summary>
public sealed class PayloadDecryptionView(IEnumerable<IPayloadDecryptor> decryptors)
{
    private readonly IReadOnlyList<IPayloadDecryptor> _decryptors = [.. decryptors];

    /// <summary>True when no decryptor is registered at all — the zero-cost default path.</summary>
    public bool IsEmpty => _decryptors.Count == 0;

    /// <summary>
    /// The view matching and templating should use for this stub: the decrypted request when the
    /// stub declares a scheme a registered decryptor handles, else the request itself.
    /// </summary>
    public CanonicalRequest For(CanonicalRequest request, PayloadDecryptDirective? directive)
    {
        if (directive is null || _decryptors.Count == 0)
        {
            return request;
        }

        foreach (var decryptor in _decryptors)
        {
            if (decryptor.Handles(directive.Scheme))
            {
                return decryptor.Decrypt(request, directive);
            }
        }

        return request;
    }
}

/// <summary>
/// A stub's declaration that its rendered response must be protected before it goes on the wire
/// (G20b, ADR 0012). <see cref="Fields"/> names the JSON fields to encrypt individually — the
/// envelope stays readable, which is what gateways and log pipelines need; an empty list means the
/// WHOLE body becomes one token, the fixed-partner shape.
/// </summary>
public sealed record PayloadProtectDirective(string Scheme, IReadOnlyList<string> Fields);

/// <summary>
/// Encrypts (and later signs) a rendered response (G20b). Like <see cref="IPayloadDecryptor"/>, the
/// implementation lives at the edge and holds the key; Core only knows that a scheme was declared.
/// Returning the response unchanged is always allowed — a mock that cannot protect its body must not
/// pretend it did.
/// </summary>
public interface IPayloadProtector
{
    /// <summary>True when this protector handles the named scheme.</summary>
    bool Handles(string scheme);

    /// <summary>
    /// Returns the protected response. Implementations must never throw: a body that cannot be
    /// protected (not JSON when field-level was asked for, for instance) is returned as it was, so
    /// serving degrades visibly rather than turning into a 500.
    /// </summary>
    CanonicalResponse Protect(CanonicalResponse response, PayloadProtectDirective directive);
}

/// <summary>
/// Applies whichever registered protector handles a stub's declared scheme (G20b). No protector for
/// the scheme means the response goes out as rendered — the same honest degradation as the
/// decryption side, and the reason a host without a key never silently ships plaintext it promised
/// to encrypt: the operator sees the plaintext immediately.
/// </summary>
public sealed class PayloadProtectionApplier(IEnumerable<IPayloadProtector> protectors)
{
    private readonly IReadOnlyList<IPayloadProtector> _protectors = [.. protectors];

    /// <summary>True when nothing is registered — the zero-cost default path.</summary>
    public bool IsEmpty => _protectors.Count == 0;

    /// <summary>The response to serve: protected when declared and handled, else as rendered.</summary>
    public CanonicalResponse For(CanonicalResponse response, PayloadProtectDirective? directive)
    {
        if (directive is null || _protectors.Count == 0)
        {
            return response;
        }

        foreach (var protector in _protectors)
        {
            if (protector.Handles(directive.Scheme))
            {
                return protector.Protect(response, directive);
            }
        }

        return response;
    }
}

/// <summary>
/// A stub's requirement that the request carry a valid signature (G20c, ADR 0012). PSD2 / Berlin
/// Group shape: a <c>Digest</c> header covers the body, and <see cref="Header"/> carries a signature
/// over that digest. Declared per stub — a request that fails the check does not match, which is how
/// a mock proves the client signed correctly instead of pretending it checked.
/// </summary>
public sealed record SignatureRequirement(string Scheme, string Header, string DigestHeader);

/// <summary>Verifies a request signature (G20c). Edge-implemented: the key never enters Core.</summary>
public interface ISignatureVerifier
{
    /// <summary>True when this verifier handles the named scheme.</summary>
    bool Handles(string scheme);

    /// <summary>
    /// True when the request carries a valid signature. Must never throw: a missing header, a
    /// malformed value or a wrong digest all read as false, because every one of them is what an
    /// unsigned or tampered request looks like.
    /// </summary>
    bool Verify(CanonicalRequest request, SignatureRequirement requirement);
}

/// <summary>
/// Decides whether a stub's signature requirement is satisfied (G20c). With no verifier registered
/// for the declared scheme the requirement **fails closed** — a stub that asks for a signature must
/// never match on a host that cannot check one, or the mock would silently accept anything.
/// </summary>
public sealed class SignatureGate(IEnumerable<ISignatureVerifier> verifiers)
{
    private readonly IReadOnlyList<ISignatureVerifier> _verifiers = [.. verifiers];

    /// <summary>True when the stub may proceed to ordinary matching.</summary>
    public bool Satisfied(CanonicalRequest request, SignatureRequirement? requirement)
    {
        if (requirement is null)
        {
            return true;
        }

        foreach (var verifier in _verifiers)
        {
            if (verifier.Handles(requirement.Scheme))
            {
                return verifier.Verify(request, requirement);
            }
        }

        return false;
    }
}

/// <summary>
/// A stub's declaration that its response must be signed (G20c). The signer adds a digest of the
/// served body plus the signature header, so a client that verifies what it receives is satisfied.
/// </summary>
public sealed record ResponseSignature(string Scheme, string Header, string DigestHeader);

/// <summary>Signs a rendered response (G20c). Edge-implemented; returns the response unchanged when it cannot sign.</summary>
public interface IResponseSigner
{
    /// <summary>True when this signer handles the named scheme.</summary>
    bool Handles(string scheme);

    /// <summary>Returns the response with its digest and signature headers added.</summary>
    CanonicalResponse Sign(CanonicalResponse response, ResponseSignature signature);
}

/// <summary>Applies whichever registered signer handles the declared scheme (G20c).</summary>
public sealed class ResponseSigningApplier(IEnumerable<IResponseSigner> signers)
{
    private readonly IReadOnlyList<IResponseSigner> _signers = [.. signers];

    /// <summary>The response to serve: signed when declared and handled, else as rendered.</summary>
    public CanonicalResponse For(CanonicalResponse response, ResponseSignature? signature)
    {
        if (signature is null || _signers.Count == 0)
        {
            return response;
        }

        foreach (var signer in _signers)
        {
            if (signer.Handles(signature.Scheme))
            {
                return signer.Sign(response, signature);
            }
        }

        return response;
    }
}

/// <summary>
/// How many keys are currently active per cryptographic capability (#250), for diagnostics.
/// </summary>
/// <remarks>
/// Counts only — key material never crosses this seam, and Core has no idea what a key looks like.
/// Read through delegates rather than captured as numbers, because with a file-backed key source the
/// answer changes while the host runs, and a stale count during a rollover would be worse than no
/// count at all: an operator would conclude the new key had not been picked up.
/// </remarks>
public sealed class ActiveKeyReport(Func<int> decryptKeys, Func<int> signKeys)
{
    /// <summary>A report for a host with no keys at all.</summary>
    public static readonly ActiveKeyReport None = new(static () => 0, static () => 0);

    /// <summary>Keys currently accepted for payload decryption and response protection.</summary>
    public int ActiveDecryptKeys => decryptKeys();

    /// <summary>Keys currently accepted for signature verification and response signing.</summary>
    public int ActiveSignKeys => signKeys();
}
