# G20 — Payload cryptography

Planned in [ADR 0012](../decisions/0012-payload-cryptography.md), tracked by issue #226. No oracle
exists — the reference engine has no payload cryptography — so every phase follows the G18/G19
precedent: a **real client library implementing the same RFC** encrypts, the wire is driven, and the
assertions prove the mock behaved. The differential suite must stay green untouched, proving the
parity surface did not move.

## G20a — field-level decryption for matching + templating

**What shipped.** A stub may declare `"decrypt": { "scheme": "jwe-dir-a256gcm", "fields": ["encData"] }`
inside its `request`. When it does, the named fields are decrypted **before** body matchers run, and
response templating sees the same decrypted view — so `matchesJsonPath` can assert on a value the
client encrypted, and `{{jsonPath request.body '$.encData.pan'}}` renders it. The scheme is JWE
compact serialization with direct key agreement and A256GCM (RFC 7516 §5.1), the shape used when
only the sensitive fields of an otherwise readable envelope are protected. The key comes from
`--decrypt-key <base64 256-bit>`.

**Decisions worth remembering.**

- **Decryption is a VIEW, never a rewrite.** The serve event keeps exactly what the client sent —
  the wire test asserts the journal still holds the ciphertext and not the plaintext. Replay, export
  and the differential harness all depend on the recorded request being verbatim; a mutation there
  would quietly corrupt all three.
- **Key material stops at the edge.** `Mockifyr.Core` holds the contract (`IPayloadDecryptor`) and
  the directive; the implementation and the key live in the new `Mockifyr.Crypto` project. Core
  keeps zero external dependencies — `AesGcm` is BCL, so no JOSE library entered the tree.
- **A payload that does not decrypt is a NON-MATCH, never an error.** Wrong key, tampered tag,
  malformed token, non-JSON body, wrong part count: every one returns the request untouched. These
  inputs are attacker-reachable by definition, so an exception escaping into a 500 would be the bug.
- **Defense in depth is deliberate.** The explicit nonce/tag length guard and the broad
  `catch (CryptographicException or FormatException or ArgumentException)` overlap on purpose:
  either alone would be sufficient, which is exactly why mutating one is not observable (see the
  survivors below). Two layers is the right trade for input an attacker chooses.
- **No decryptor, no magic.** Without `--decrypt-key`, a stub that declares `decrypt` simply does
  not match — the honest outcome for a host that was never given the key, rather than a silent
  plaintext match.
- **Zero effect on every other stub.** A stub without the declaration takes the identical code path
  (the engine reuses the very same `MatchInput` instance), which the wire suite asserts directly.

**Validation story.** `JweFieldDecryptorTests` (11 unit tests, encrypting with an independent
implementation of RFC 7516 §5.1: object and string payloads, wrong key, tampered tag, malformed and
wrong-part-count tokens, wrapped-key refusal, short nonce/tag, non-JSON and field-less bodies, the
key reader's base64/base64url/short-key handling, and the scheme-selection view) plus
`PayloadDecryptionWireTests` (4 end-to-end tests against a real host: matching on an encrypted field
with templated echo, two different ciphertexts of the same plaintext both matching — the case
`binaryEqualTo` cannot express at all, a foreign-key payload not matching while the journal keeps
the ciphertext, and an undeclared stub behaving exactly as before).

**Stryker: 26/29** on the decryptor. The three survivors are the redundant-by-design pair described
above — the nonce/tag length guard and its enclosing catch. With both layers present, removing
either produces the same observable result (no decryption, no match), so no test can distinguish
them; removing *both* would be observable, and that combination is not a mutant Stryker generates.

**Deferred (tracked in ADR 0012).** G20b response protection, G20c signing, G20d whole-body inbound
decryption. Also deferred: multiple keys / key rotation, per-tenant keys, and wrapped-key JWE
(`alg != dir`), which is explicitly refused today rather than half-supported.


## G20b — response protection

**What shipped.** A stub may declare `"protect": { "scheme": "jwe-dir-a256gcm", "fields": ["encData"] }`
inside its `response`. Named fields are encrypted individually (the envelope stays readable — what
gateways, routing and log pipelines need); **naming no field encrypts the whole body** as one token,
the fixed-partner shape. The same `--decrypt-key` serves both directions: a partner that encrypts
what it sends also decrypts what it receives, and asking for two keys per relationship would be
ceremony without a security benefit.

**Decisions worth remembering.**

- **Protection runs LAST** — after templating and after every response transformer — so what gets
  encrypted is exactly what would otherwise have gone on the wire. The wire test pins this: a
  templated `{{jsonPath request.body …}}` value decrypts back out of the ciphertext.
- **The serve event records the PROTECTED response**, unlike the request side which records the
  verbatim ciphertext. Both rules follow one principle: the journal holds what actually crossed the
  wire in each direction.
- **A fresh nonce per token, always.** Reusing a nonce under one key voids GCM's confidentiality
  guarantee entirely, so this is asserted directly: two protections of the same plaintext must
  differ, and both must decrypt to it.
- **Visible degradation over silent fallback.** Field-level protection asked for on a body that has
  no fields (not JSON, an array, or the field simply absent) serves the response **as rendered**
  rather than quietly switching to whole-body protection — the operator sees plaintext immediately
  and fixes the stub. A mock that pretends it encrypted is worse than one that visibly did not.
- **Scalar vs structured fields:** a string field is encrypted as its raw value, an object/array as
  its JSON text — exactly what the decryption side expects to find on the way back, so the two
  halves compose without special cases.

**Validation story.** `JweResponseProtectorTests` (6 unit tests, every assertion **decrypting with
the paired implementation**: field-level with a surviving envelope, scalar round-trip, whole-body
token, fresh-nonce proof, the four degradation cases, scheme selection through the applier) plus
`ResponseProtectionWireTests` (4 end-to-end tests against a real host: templated field encrypted on
the way out, the **full round trip** — encrypted in, matched on plaintext, encrypted out, which is
the shape a bank integration actually has — whole-body protection, and an undeclared stub still
serving plaintext). **Stryker: 100 %.**

**Deferred (tracked in ADR 0012).** G20c signing, G20d whole-body inbound decryption; plus content
negotiation (an `application/jose` content type on protected responses) and per-field schemes.
