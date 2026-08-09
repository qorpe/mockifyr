# Scenarios: the state machine behind "same request, different answer"

## Photo vs. film

A plain stub is a photograph — same request, same answer, forever. A **scenario** adds the
time axis: the answer depends on where you are in a flow, and answering can move the flow.

The demo's payment-status pair:

```json
{ "scenarioName": "payment-PAY-1001",
  "requiredScenarioState": "Started", "newScenarioState": "Settled",
  "request":  { "method": "GET", "urlPath": "/api/payments/PAY-1001/status" },
  "response": { "jsonBody": { "status": "pending" } } }

{ "scenarioName": "payment-PAY-1001",
  "requiredScenarioState": "Settled",
  "request":  { "method": "GET", "urlPath": "/api/payments/PAY-1001/status" },
  "response": { "jsonBody": { "status": "settled" } } }
```

Call the endpoint twice: `pending`, then `settled` (and it stays settled — the second stub
declares no `newScenarioState`, so it's terminal).

## The rule — two gates

Scenario state does not replace matching; it is an extra gate on top:

```
1st gate  request pattern   method + URL + headers + body must all match
2nd gate  scenario state    current state must equal requiredScenarioState
→ winner answers → its newScenarioState (if any) advances the flow
```

So you can combine them: "state is Settled AND header X-Channel: mobile → this answer".
A stub with no scenario fields has no 2nd gate. If no stub fits the current state, the
request is honestly unmatched (404) — which itself catches "this call shouldn't happen at
this step of the flow".

## What it's for — three concrete uses

1. **Polling flows** (the demo): payment status, shipment tracking, async report generation.
   Without state you cannot test the client's "wait, retry, continue" logic at all.
2. **Resilience**: "first call fails with 500, the retry succeeds" — two stubs chained by a
   scenario prove your retry/backoff/circuit-breaker actually works.
3. **Multi-step order enforcement**: confirm-before-pay style mistakes surface as 404s
   because the confirming stub isn't eligible until the paying stub ran.

## Driving it

State lives server-side per (tenant, scenario) — every client sees the same frame. It is a
**test fixture**: inspect (`GET /__admin/scenarios` → current state + derived possible
states), set (`PUT /__admin/scenarios/{name}/state` — the dashboard's clickable pills), and
reset all (`POST /__admin/scenarios/reset`). Set the state directly to test a mid-flow
situation without walking the flow from the start ("a user on onboarding step 4").
Deliberately not persisted across restarts: fixtures must start clean, not inherit a
half-finished flow.

Bonus: the recorder emits scenario chains automatically — record the same URL three times
with different answers and the snapshot replays them in order. See [recording.md](recording.md).
