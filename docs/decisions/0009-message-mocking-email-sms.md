# 0009 — Message mocking: email and SMS become first-class capture channels

## Status

Accepted. Design for the G18 roadmap group (epic tracked on GitHub); implemented incrementally by
the G18a–G18f verticals.

## Context

Mockifyr mocks request/response protocols: HTTP, and via facade-edge codecs also gRPC, GraphQL and
WebSocket messages. But applications under test do not only *call* APIs — they *send things*:
transactional email (SMTP) and SMS (through provider HTTP APIs such as Twilio). Today a team
testing an OTP login flow needs three tools: Mockifyr for the APIs, Mailpit/MailHog for mail
capture, and hand-written stubs for the SMS provider — with three UIs, three query APIs and no
shared tenant model.

The observation that unlocks the design: mocking an outbound message channel is **two mocks in
one** —

1. **Protocol mock** — the application sends a message and must receive a realistic answer
   (SMTP `250 OK`, or a Twilio-shaped `{"sid": …, "status": "queued"}` JSON), including the
   failure modes (SMTP `550`, provider error codes).
2. **Capture + verify** — every accepted message lands in a queryable, tenant-scoped inbox:
   who received it, what it contained, what the OTP was. Tests assert on it through the admin
   API; humans browse it in the dashboard.

Neither WireMock nor any mock-server clone offers this; mail sinks offer no API mocking. One tool
doing both, tenant-aware and scenario-aware, is a genuine differentiator.

Constraints from the existing architecture (CLAUDE.md golden rules):

- The engine stays pure — no SMTP, MIME or provider knowledge in `Mockifyr.Core`.
- Multi-tenancy is first-class — every store entry point takes a `TenantId`.
- Transport never leaks inward — SMTP is a facade, exactly as Kestrel is for HTTP.
- No stable oracle exists: WireMock cannot serve SMTP or emulate Twilio. Like WebSocket (G15),
  these verticals are validated by **real-client self-tests** (MailKit as an SMTP client, the
  official Twilio C# SDK pointed at Mockifyr), stated explicitly in `docs/parity/`.

## Decision

### A message is a domain value, not a transport artifact

`Mockifyr.Core` gains a pure model:

- `MessageEnvelope` — `Id`, `Channel` (`Email` | `Sms`), `From`, `To` (list), `Subject`
  (email-only), `Body` (text), `HtmlBody` (email-only), `Headers`/`Meta` (flat string map — SMS
  provider fields like `MessagingServiceSid` go here), `Attachments` (name, content type, size,
  content), `ReceivedAt`.
- `IMessageStore` — tenant-scoped append/list/get/delete/reset with a bounded capacity
  (ring-buffer semantics: oldest evicted first). No tenant-less overload.
- `IMessageSink` — the write-side seam a facade calls; the default sink appends to the store and
  raises a serve-event so the existing webhook infrastructure (G3) can notify listeners.

The transports translate **at the edge**: the SMTP facade parses MIME (MimeKit, MIT-licensed,
facade-only dependency) into an envelope; the SMS profile parses the provider's wire format.
Core never sees either format — the same shape as the gRPC descriptor codec (ADR/G13).

### Email arrives over a real SMTP listener; SMS arrives over provider-shaped HTTP

- **`Mockifyr.Facade.Smtp`** — an opt-in listener (`--smtp-port`, no default). It speaks enough
  ESMTP for real clients (EHLO, MAIL FROM, RCPT TO, DATA, QUIT; AUTH accepted-but-unchecked),
  parses the DATA payload with MimeKit, and hands the envelope to `IMessageSink`. Fault
  directives (reject with 550, delay, drop) are applied by the facade — mirroring how HTTP
  delay/fault are facade directives, not engine logic.
- **SMS has no wire protocol of its own** — real applications send SMS through provider HTTP
  APIs. So SMS mocking is a **provider profile** on the existing HTTP facade: an opt-in flag
  (`--sms-profile twilio`) mounts Twilio-shaped routes (`POST /2010-04-01/Accounts/{sid}/Messages.json`),
  parses the form body into an SMS envelope, stores it, and answers with a realistic Twilio
  response the official SDK accepts. Other profiles (Vonage, NetGSM, …) follow the same seam.
  A user can still stub any provider URL by hand today; the profile adds capture + realistic
  responses without writing stubs.

### Tenant resolution mirrors the HTTP facade

SMS inherits HTTP tenant resolution unchanged (header/host). SMTP resolves the tenant from, in
order: the AUTH username when presented, else the recipient domain when it matches a configured
tenant domain (multi-domain, G15), else the default tenant. The rule lives in the facade.

### Verify is a sibling of `/__admin/requests`

`/__admin/messages` (CQRS through `Mockifyr.Application`, REST in `Mockifyr.Facade.Admin`):
list with channel/recipient/text filters, get, delete, reset, count — plus an OTP-extraction
helper (`GET /__admin/messages/{id}/otp?pattern=…`, default pattern `\b\d{4,8}\b`) so an e2e
test turns "wait for the SMS and read the code" into one HTTP call.

### The dashboard gets a Messages section, not more stub screens

Captured messages are traffic, not stubs — the UI treats them like the Journal: an inbox
(list/search/filter), a detail view (HTML preview rendered in a sandboxed iframe, source,
headers, attachments), an SMS thread view per recipient number with OTP badges. Channel
*behaviors* (SMTP faults, provider error simulation) are configured in this section too.
Stub screens are unaffected (see ADR 0010 for how stub *protocols* are surfaced).

## Consequences

- Two new opt-in surfaces (SMTP port, SMS profile) — **nothing changes for existing users**;
  no flag, no listener, no new routes.
- New facade-edge dependency: MimeKit (MIT — Apache-2.0-compatible), never referenced by Core.
- The message store starts in-memory with a bounded capacity; durable persistence reuses the
  G16 seam later if demanded — explicitly deferred, recorded per-vertical in `docs/parity/`.
- No differential oracle: correctness claims for these verticals rest on real-client
  self-tests plus unit/integration/mutation coverage, and the docs must say so — the same
  honesty rule the WebSocket facade follows.
- Streaming/POP3/IMAP retrieval and the SMPP telco protocol are out of scope and tracked as
  deferred edges.
