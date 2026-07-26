# G18 — Message mocking (email + SMS) and the protocol-aware surface

G18 is Mockifyr surface, not WireMock dialect: WireMock has no SMTP listener, no SMS provider
emulation, no message inbox, and no protocol classification. **There is no oracle for anything in
this group** — every vertical is validated by real-client self-tests plus unit/integration coverage,
and this file states exactly what was verified per vertical (the same honesty rule G15's WebSocket
serving follows).

## G18-pre — Protocol-aware stub UX (ADR 0010)

- **Group / item:** G18-pre — self-tested (`G18PreProtocolUxTests`, host-only, no Docker;
  `G18PreStubProtocolsTests` for the pure decision table). Verified in-browser on the dashboard.
- **The `protocol` field is computed, never stored.** `GET /__admin/mappings` stamps
  `protocol: http|grpc|graphql` per mapping at query time. Verified: the persisted document
  contains exactly the posted mapping plus the id/uuid stamp G16 always writes — no protocol key —
  and its `request` is deep-equal to what was posted. (Learned in passing: persisted files are
  *re-serialized* by `PersistableJson` — key set and values survive byte-exactly, but string
  escaping may differ, e.g. `\"` becomes `"`. Assertions about "as-is" storage must compare
  JSON-semantically, not byte-wise.)
- **Classification rules.** `graphql` = the stub carries the `graphql-body-matcher` custom matcher
  (name compared case-insensitively; any other custom matcher name is not GraphQL). `grpc` = the
  stub's plain `urlPath`/`url` (query string stripped) resolves against a loaded descriptor —
  pattern URL forms are never probed, and without its descriptor a gRPC-shaped stub classifies
  (honestly) as `http`, since it could not serve gRPC anyway. GraphQL wins when both would apply.
  The probe crosses the facade boundary as `IStubProtocolProbe`, implemented in the composition
  root — the admin and gRPC facades stay unacquainted.
- **Descriptor admin + hot reload.** `GET/POST/DELETE /__admin/grpc/descriptors` manage
  `<root-dir>/grpc/*.dsc`. `ProtoDescriptors` became swappable (volatile index snapshot): an upload
  is parse-validated *before* anything is written (garbage → 422, existing index untouched), then
  the index rebuilds from the directory. Verified end-to-end: a host started with **no** grpc
  directory serves a real `SayHello` gRPC call immediately after an upload — no restart — and
  delete empties the index again. The middleware is now registered whenever a root-dir exists;
  it still only engages for `application/grpc` requests.
- **Message-mappings listing.** `GET /__admin/message-mappings` returns each registration JSON
  as posted with the id stamped in (the stub-list shape); `DELETE /{id}` removes it (404 on a
  second delete). Tenant-scoped: another tenant's list is empty. `MessageMapping` retains its
  raw `Source` for this — serving behavior is unchanged (G15 tests still green).
- **Dashboard.** Protocol chips (gRPC/GraphQL/WS; HTTP intentionally unmarked) + a protocol facet
  on the stub tree; the Add flow starts with a channel choice — HTTP is the unchanged classic
  editor, gRPC/GraphQL/WebSocket are forms that emit the exact dialect JSON (live, editable
  preview); WebSocket mappings are listed under the tree with a read-only detail sheet; Settings
  gained a gRPC-descriptors card (upload/list/delete). All six locales translated; verified
  in-browser against a live host (chips, facet, gRPC form fed by descriptors, WS create → listed).
- **Deferred (tracked, not silent):** editing an existing WS mapping (list/delete/create only —
  no PUT endpoint exists); gRPC request-skeleton generation from the descriptor's input type in
  the form; protocol chips in the journal.

## G18a — Core message model + store + admin API (ADR 0009)

- **Group / item:** G18a — self-tested (`G18aMessageStoreTests` unit + CQRS, `G18aMessagesAdminTests`
  over the wire); **mutation-tested with Stryker.NET**: 100% score on `MessageOperations`/
  `MessageHandlers` (36 mutants), `InMemoryMessageStore` (19), and G18-pre's `StubProtocols` (19) —
  survivors were killed by adding tests (Limit=0 semantics, NotFound error codes, any-recipient
  matching, at-capacity no-eviction, urlPath-over-url precedence, query-only URL) and by one
  refactor: the eviction `RemoveRange(count - Capacity)` was an *equivalent-mutant* shape
  (`RemoveRange(0,0)` no-ops), rewritten to `RemoveAt(0)` which is both simpler and testable.
- **Model.** `MessageEnvelope` (channel `email`|`sms`, from/to/subject/body/htmlBody, flat `Meta`
  map for provider fields, attachments, receivedAt) + `IMessageStore`/`IMessageSink` in Core —
  pure, zero deps; the in-memory store is bounded per tenant (default 1000, oldest evicted first,
  newest-first reads) and strictly tenant-scoped (cross-tenant get/remove refuse, verified).
- **Admin surface.** `/__admin/messages` (+`/count`, `/{id}`, `DELETE /{id}`, `POST /reset`) via
  CQRS; filters (`channel`, `recipient` any-addressee case-insensitive substring, `contains` over
  subject+bodies, `limit` where 0 = unlimited) are defined once in `MessageFilter` and shared by
  list and count, so the two can never disagree. Attachment content is not inlined in JSON
  (name/type/size only; download endpoint lands with the inbox UI, G18c).
- **Learned in passing:** Stryker.NET (4.16) only offers as mutable the projects a test csproj
  references **directly** — transitive references through `Mockifyr.Server` are invisible to it.
  The test project now references Application/Stores.InMemory/Facade.Admin directly (harmless,
  already transitive) to make them mutable.
- **Deferred (tracked):** durable message persistence (reuse the G16 seam if demanded); attachment
  download endpoint (G18c); verify/OTP query shapes (G18f).
