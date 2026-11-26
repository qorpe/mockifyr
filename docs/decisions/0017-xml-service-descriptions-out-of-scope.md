# 0017 — No WSDL import and no ISO 20022 validation (for now)

## Status

Accepted — 2026-11-25, by the owner, on issue [#462](https://github.com/qorpe/mockifyr/issues/462). Recorded
2026-11-26. Supersedes nothing. Changes no behaviour: it records what the integration sandbox (ADR 0011) does
**not** take on, so that the absence is a decision rather than a gap somebody rediscovers.

## Context

The sandbox-parity review of 2026-11-17 asked what a partner-facing sandbox needs before it can stand in for a
bank's or a card scheme's developer platform. One question it could not settle on evidence alone was XML service
descriptions:

- **What exists.** XML request bodies are matched with `equalToXml` (with XMLUnit placeholders) and `matchesXPath`
  (with XPath functions and namespaces), both verified against the reference engine (`docs/parity/g1-matching.md`,
  G1g). A SOAP request can be matched and answered today, one stub per operation, written by hand.
- **What does not.** There is no WSDL import that turns a service description into stubs, no validation of an XML
  message against an XSD at serve time, and no `soapXPath` helper (it returns empty on the reference engine for
  every SOAP form tried, `docs/parity/g2-response.md`). ISO 20022 and ISO 8583 appear nowhere in the code.
- **Why the question is real.** Banking back ends still expose SOAP services and ISO 20022 messages (XML over HTTP).
  Whether a partner-facing sandbox needs them depends on the deployment: most partner channels are REST with JSON,
  and the OpenAPI import (G19c) already serves those.

Building either capability is not small. A WSDL import means a document/literal binding reader, an XSD-to-example
generator and a new admin route. Serve-time ISO 20022 validation means shipping, versioning and updating the
message schemas, or accepting them per tenant, plus a fault shape. Neither has a user asking for it today.

## Decision

**Neither is in scope for now.**

1. **No WSDL import.** A SOAP service is mocked as it is today: stubs whose request matches with `matchesXPath`
   or `equalToXml`, and whose response is the SOAP envelope as a literal or templated body.
2. **No ISO 20022 validation.** An ISO 20022 message is matched as XML like any other. Nothing checks it against
   its XSD, and a malformed message gets whatever the matching stub answers.
3. **ISO 8583 is out of scope outright.** It is a binary card protocol carried over TCP, not HTTP, so it is not a
   request this host can receive. That holds whatever later happens to items 1 and 2.

## Consequences

- The deferred-edge register (`docs/parity/deferred-edges.md`) lists all three under **Accepted**, pointing here.
  The website's limitations page is to say the same.
- The decision is "not now", not "never", for items 1 and 2. They return as one issue, decided again, when a
  deployment brings an XML-first partner channel, with the services and the message set it uses. That issue
  starts from this record. Item 3 does not return.
- Nothing moves in Core. A future WSDL import would sit beside the OpenAPI adapter (`Mockifyr.Adapters.OpenApi`),
  and XSD validation behind a pure Core seam with the schema work at the edge. That is the same shape ADR 0011 and
  ADR 0012 already use, so declining today closes off no design.
