# Beyond HTTP: gRPC, GraphQL, WebSocket

One matching/templating engine, one stub list, one journal — different wires at the edge.
The Stubs tree shows all four channels side by side with protocol chips.

## gRPC

- **Setup**: upload a protobuf descriptor set — `POST /__admin/grpc/descriptors?name=greeter`
  with the raw `.dsc` bytes (produced by `protoc --include_imports --descriptor_set_out`).
  Serving **hot-enables**: `{"serving": true}`, no restart. Invalid bytes → 422, index untouched.
- **A gRPC stub is an ordinary stub**: the URL is `/{package.Service}/{Method}`, the body
  matcher sees the request message as JSON, the reply comes from `jsonBody`:

```json
{ "request":  { "method": "POST", "urlPath": "/mockifyr.grpc.test.Greeter/SayHello",
                "bodyPatterns": [ { "equalToJson": "{ \"name\": \"Ada\" }" } ] },
  "response": { "status": 200, "jsonBody": { "message": "Hello Ada" } } }
```

- Full codec: nested messages, enums by name, maps, repeated (packed/unpacked), `oneof`,
  well-known wrappers; 64-bit ints as JSON strings. Real gRPC error statuses via
  `grpc-status-name` / `grpc-status-reason` response headers. Tenant rides call metadata
  (`x-mockifyr-tenant`). gRPC needs HTTP/2 — the demo serves it over TLS on :8443.

## GraphQL

A GraphQL stub matches on the tuple (query, variables, operationName) via a built-in matcher:

```json
{ "customMatcher": { "name": "graphql-body-matcher", "parameters": {
    "query": "query Payment($id: ID!) { payment(id: $id) { id status amount } }",
    "variables": { "id": "PAY-1001" }, "operationName": "Payment" } } }
```

The key property: queries are compared **as parsed trees (AST), not text**. Whitespace,
field order and argument order are irrelevant — the demo's `graphql-messy` step sends the
same query minified and reordered, and it still matches. Real clients format queries
however they like; the stub doesn't care. Responses can be templated from the request body
(e.g. copying `variables.id` into the answer).

Gotcha: a stub that omits `variables`/`operationName` only matches requests that omit them
too — seed both fields, real clients always send them.

## WebSocket

Connect to any path on the mock port; the tenant is read from the handshake headers.
"Stubs" here are **message mappings** (`POST /__admin/message-mappings`):

```json
{ "trigger": { "type": "message", "message": { "body": { "equalTo": "ping" } } },
  "actions": [ { "type": "send", "message": { "body": { "data": "pong" } } } ] }
```

- `trigger.type: "connection"` pushes a message the moment a client connects.
- Triggers use the standard body-matcher set (`equalTo`, `matches`, `equalToJson`, …).
- Replies are templated (`{{message.body}}`), target the originating socket or **broadcast**
  to every socket of the tenant. `POST /__admin/channels/send` pushes server-initiated
  messages to all of a tenant's connections.
