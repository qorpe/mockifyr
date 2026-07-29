# 0012 — Payload cryptography: field-level encryption, whole-body encryption, signing

Status: **Proposed** (planning only — no implementation is authorized by this ADR)
Date: 2026-07-29
Issue: #226 · Roadmap group: **G20**

## Context

A large class of enterprise upstreams does not send plaintext JSON over TLS. They apply cryptography
to the payload itself, on top of the transport, because a partner's security review mandates
protection independent of TLS. It appears in three shapes, and they combine in practice:

| Shape | On the wire | Where it is met |
|---|---|---|
| **Field-level encryption** | Readable JSON envelope, named fields replaced by ciphertext | Card-scheme field-level encryption, wallet tokens nested in an otherwise readable payload, PCI designs where only PAN/CVV are protected |
| **Whole-body encryption** | The entire body is one JWE token or AEAD envelope | Bank-to-bank and fixed-partner B2B integrations |
| **Signing** | Body may be plaintext, but a signature header must verify | PSD2 / Berlin Group (`Digest` + `X-JWS-Signature`), HMAC request signatures, WS-Security on SOAP |

Field-level is the most common: the envelope must stay readable for gateways, routing, rate limiting
and log pipelines, so only the sensitive fields are encrypted.

### What happens today

- **Matching:** every body matcher (`equalToJson`, `matchesJsonPath`, `matchesXPath`,
  `matchesJsonSchema`) sees ciphertext. With whole-body encryption only `binaryEqualTo` remains, and
  a correct client uses a random IV per request — so the body differs every time and matching cannot
  succeed at all, rather than merely degrading.
- **Templating:** `{{jsonPath request.body '$.x'}}` reads the same ciphertext, so a response cannot
  correlate with what the client actually sent.
- **Responses:** nothing can encrypt or sign what is served, so a client that decrypts or verifies
  the response rejects the mock.

This is exactly the integration profile of the sandbox work (ADR 0011) — a partner sandbox for a bank
that mandates payload encryption is unusable without it.

## Decision (shape, not schedule)

Payload cryptography becomes its own vertical, **G20**, delivered in phases behind explicit stub
opt-in. Nothing about it is implicit: a stub that does not declare cryptography behaves exactly as
today, and the engine's purity rules do not move.

**The seam.** Cryptography is a *request pre-processing* and *response post-processing* concern, not
a matcher feature. Two Core contracts, both pure and both edge-implemented:

- `IPayloadDecryptor` — given the canonical request and a declared scheme, returns a **decrypted
  view** of the body. Matching and templating run against that view; the original bytes stay on the
  serve event (subject to journal masking, #227).
- `IPayloadProtector` — given the rendered response and a declared scheme, returns the encrypted
  and/or signed body plus any headers it must add (`Digest`, `X-JWS-Signature`, …).

Key material lives at the host edge (`Mockifyr.Server`), never in Core: files under `<root-dir>/keys`
or a KMS-shaped seam later. Core sees an abstract "scheme applied", never a private key.

**Why a view rather than rewriting the request:** the recorded request must stay what the client
actually sent — replay, export and the differential harness all depend on that. Decryption is a lens
matching looks through, not a mutation.

### Phases

1. **G20a — field-level decryption for matching + templating.** JWE-per-field and AES-GCM with a
   configured key; declared per stub (`request.decrypt: { scheme, fields }`). The envelope keeps
   matching as it does today; the named fields become matchable and templatable.
2. **G20b — response protection.** `response.protect: { scheme, fields }` for field-level, and
   whole-body JWE. Enables a client that decrypts what it receives.
3. **G20c — signing.** Request signature *verification* as a matcher (`signatureValid`), and
   response signing (`Digest` + detached JWS, HMAC). PSD2-shaped profiles as presets, the way the
   Twilio SMS profile packages a vendor shape (ADR 0009).
4. **G20d — whole-body inbound decryption**, once the phases above have settled the key handling.

### Validation strategy

No oracle exists for any of this (the reference engine has no payload cryptography), so this follows
the G18/G19 precedent: **real-client self-tests** — encrypt with a standard library (the same JOSE
implementation a partner would use), drive the wire, assert the mock matched and that what it
returned decrypts and verifies with that library. Plus unit tests and Stryker on the pure logic, and
the full differential suite staying green to prove the parity surface did not move.

## Consequences

- The engine gains a declared, opt-in pre/post-processing seam; a stub without the declaration is
  bit-for-bit unaffected — the same fence every vertical since G18 has used.
- Key management becomes a host concern with an operational surface (rotation, per-tenant keys),
  which is why it is phased rather than shipped at once.
- Until G20 lands, the honest answer for an encrypted-payload integration is: mock the endpoint with
  the encryption turned off in the client's sandbox configuration, or wait for this vertical. That
  limitation is now documented rather than discovered.

## Alternatives considered

- **Matcher-level decryption** (a `decryptedJsonPath` matcher): rejected — it would duplicate key
  handling in every matcher and leave templating and responses unsolved.
- **Extension-only** (leave it to `IResponseTransformer`/custom matchers): rejected as the *whole*
  answer — the seams exist, but every enterprise user would rebuild the same JOSE plumbing, and the
  request-side view is not reachable from those seams today. Extensions remain the escape hatch for
  bespoke schemes.
- **Ship a single "crypto" flag**: rejected — the three shapes have genuinely different contracts;
  one flag would fit none of them well.
