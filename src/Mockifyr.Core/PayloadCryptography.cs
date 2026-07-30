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
