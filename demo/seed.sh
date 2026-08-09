#!/usr/bin/env bash
# Seeds the demo state from scratch. Safe to re-run — it resets first.
# Requires: run-server.sh (:8080) and run-upstream.sh (:9090) already running.
set -euo pipefail

MAIN="${MOCKIFYR_MAIN:-http://localhost:8080}"
UP="${MOCKIFYR_UP:-http://localhost:9090}"
ACME='X-Mockifyr-Tenant: acme-pay'
GLOBEX='X-Mockifyr-Tenant: globex'
JSON='Content-Type: application/json'

say() { printf '\n\033[1;36m== %s\033[0m\n' "$*"; }

# SSO mode: admin calls carry a bearer token (see demo.sh for the full note).
if [ -f "$(dirname "$0")/.sso" ]; then
  SSO_TOKEN=$("$(dirname "$0")/oidc/token.sh" 2>/dev/null || true)
  SSO_TOKEN_GLOBEX=$("$(dirname "$0")/oidc/token.sh" globex 2>/dev/null || true)
fi
curl() {
  if printf '%s' "$*" | grep -q "__admin"; then
    if [ -n "${SSO_TOKEN:-}" ]; then
      if printf '%s' "$*" | grep -q "globex"; then
        command curl "$@" -H "Authorization: Bearer $SSO_TOKEN_GLOBEX"
      else
        command curl "$@" -H "Authorization: Bearer $SSO_TOKEN"
      fi
      return
    fi
    if [ -n "${MOCKIFYR_BASIC:-}" ]; then
      command curl "$@" -u "$MOCKIFYR_BASIC"
      return
    fi
  fi
  command curl "$@"
}

say "Reset acme-pay + globex on the main host"
for H in "$ACME" "$GLOBEX"; do
  curl -sf -X POST "$MAIN/__admin/mappings/reset"   -H "$H" > /dev/null
  curl -sf -X DELETE "$MAIN/__admin/requests"       -H "$H" > /dev/null
  curl -sf -X POST "$MAIN/__admin/messages/reset"   -H "$H" > /dev/null
  curl -sf -X POST "$MAIN/__admin/resources/reset"  -H "$H" > /dev/null
  curl -sf -X POST "$MAIN/__admin/scenarios/reset"  -H "$H" > /dev/null
  curl -sf -X DELETE "$MAIN/__admin/clock"          -H "$H" > /dev/null
  curl -sf -X DELETE "$MAIN/__admin/degradation"    -H "$H" > /dev/null || true
done
# WebSocket message-mappings have no reset — delete one by one.
for ID in $(curl -sf "$MAIN/__admin/message-mappings" -H "$ACME" | jq -r '.messageMappings[].id'); do
  curl -sf -X DELETE "$MAIN/__admin/message-mappings/$ID" -H "$ACME" > /dev/null
done
# API keys persist across restarts — remove old ones so exactly one ci-pipeline key exists.
for ID in $(curl -sf "$MAIN/__admin/apikeys" -H "$ACME" | jq -r '.keys[].id // empty'); do
  curl -sf -X DELETE "$MAIN/__admin/apikeys/$ID" -H "$ACME" > /dev/null
done

say "Seed acme-pay: order stub (matching anatomy — header + JSONPath body matcher, templated reply)"
curl -sf -X POST "$MAIN/__admin/mappings" -H "$ACME" -H "$JSON" -d '{
  "name": "Create order — partner key + body validation",
  "request": {
    "method": "POST",
    "urlPath": "/api/orders",
    "headers": { "X-Partner-Key": { "equalTo": "secret" } },
    "bodyPatterns": [ { "matchesJsonPath": { "expression": "$.sku", "equalTo": "WIDGET-1" } } ]
  },
  "response": {
    "status": 201,
    "transformers": ["response-template"],
    "jsonBody": {
      "orderId": "ORD-7001",
      "sku": "{{jsonPath request.body '\''$.sku'\''}}",
      "qty": "{{jsonPath request.body '\''$.qty'\''}}",
      "status": "accepted"
    }
  }
}' | jq -c .

say "Seed acme-pay: authorize stub with a callback webhook (answers 202, then POSTs the callback)"
curl -sf -X POST "$MAIN/__admin/mappings" -H "$ACME" -H "$JSON" -d '{
  "name": "Authorize payment — fires payment-status callback",
  "request": { "method": "POST", "urlPath": "/api/payments/authorize" },
  "response": { "status": 202, "jsonBody": { "status": "authorizing" } },
  "postServeActions": [ {
    "name": "webhook",
    "parameters": {
      "method": "POST",
      "url": "http://localhost:8080/callbacks/payment-status",
      "headers": { "Content-Type": "application/json", "X-Mockifyr-Tenant": "acme-pay" },
      "body": "{\"paymentId\":\"{{jsonPath originalRequest.body '\''$.paymentId'\''}}\",\"status\":\"AUTHORIZED\"}",
      "delay": { "type": "fixed", "milliseconds": 2000 }
    }
  } ]
}' | jq -c .

curl -sf -X POST "$MAIN/__admin/mappings" -H "$ACME" -H "$JSON" -d '{
  "name": "Callback receiver (stands in for the merchant system)",
  "request": { "method": "POST", "urlPath": "/callbacks/payment-status" },
  "response": { "status": 200, "jsonBody": { "received": true } }
}' | jq -c .

say "Seed acme-pay: payment status scenario (pending -> settled)"
curl -sf -X POST "$MAIN/__admin/mappings/import" -H "$ACME" -H "$JSON" -d '{ "mappings": [
  { "name": "Payment status — pending (first poll)",
    "scenarioName": "payment-PAY-1001", "requiredScenarioState": "Started", "newScenarioState": "Settled",
    "request": { "method": "GET", "urlPath": "/api/payments/PAY-1001/status" },
    "response": { "status": 200, "jsonBody": { "id": "PAY-1001", "status": "pending" } } },
  { "name": "Payment status — settled (after the first poll)",
    "scenarioName": "payment-PAY-1001", "requiredScenarioState": "Settled",
    "request": { "method": "GET", "urlPath": "/api/payments/PAY-1001/status" },
    "response": { "status": 200, "jsonBody": { "id": "PAY-1001", "status": "settled" } } }
] }' | jq -c .

say "Seed acme-pay: GraphQL stub (AST-normalized matching)"
curl -sf -X POST "$MAIN/__admin/mappings" -H "$ACME" -H "$JSON" -d '{
  "name": "GraphQL — payment query",
  "request": {
    "method": "POST",
    "urlPath": "/graphql",
    "customMatcher": {
      "name": "graphql-body-matcher",
      "parameters": {
        "query": "query Payment($id: ID!) { payment(id: $id) { id status amount } }",
        "variables": { "id": "PAY-1001" },
        "operationName": "Payment"
      }
    }
  },
  "response": {
    "status": 200,
    "transformers": ["response-template"],
    "body": "{\"data\":{\"payment\":{\"id\":\"{{jsonPath request.body '\''$.variables.id'\''}}\",\"status\":\"settled\",\"amount\":149.5}}}"
  }
}' | jq -c .

say "Seed acme-pay: token stub reading the tenant clock"
curl -sf -X POST "$MAIN/__admin/mappings" -H "$ACME" -H "$JSON" -d '{
  "name": "Token endpoint — issued/expires from the tenant clock",
  "request": { "method": "GET", "urlPath": "/api/token" },
  "response": {
    "status": 200,
    "transformers": ["response-template"],
    "jsonBody": { "issuedAt": "{{now}}", "expiresAt": "{{now offset='\''1 hours'\''}}" }
  }
}' | jq -c .

say "Seed acme-pay: gRPC stub (serves once the descriptor is uploaded live)"
curl -sf -X POST "$MAIN/__admin/mappings" -H "$ACME" -H "$JSON" -d '{
  "name": "gRPC Greeter.SayHello",
  "request": {
    "method": "POST",
    "urlPath": "/mockifyr.grpc.test.Greeter/SayHello",
    "bodyPatterns": [ { "equalToJson": "{ \"name\": \"Ada\" }" } ]
  },
  "response": { "status": 200, "jsonBody": { "message": "Hello Ada — served by a Mockifyr stub" } }
}' | jq -c .

say "Seed acme-pay: WebSocket message mappings (connect greeting, ping/pong, broadcast)"
curl -sf -X POST "$MAIN/__admin/message-mappings" -H "$ACME" -H "$JSON" -d '{
  "trigger": { "type": "connection" },
  "actions": [ { "type": "send", "message": { "body": { "data": "welcome to the acme-pay stream" } } } ]
}' | jq -c .
curl -sf -X POST "$MAIN/__admin/message-mappings" -H "$ACME" -H "$JSON" -d '{
  "trigger": { "type": "message", "message": { "body": { "equalTo": "ping" } } },
  "actions": [ { "type": "send", "message": { "body": { "data": "pong" } } } ]
}' | jq -c .
curl -sf -X POST "$MAIN/__admin/message-mappings" -H "$ACME" -H "$JSON" -d '{
  "trigger": { "type": "message", "message": { "body": { "equalTo": "shout" } } },
  "actions": [ { "type": "send", "channelTarget": { "type": "broadcast" },
                 "message": { "body": { "data": "everyone: {{message.body}} heard" } } } ]
}' | jq -c .

say "Seed acme-pay: sandbox resources (payments collection)"
curl -sf -X POST "$MAIN/__admin/resources/payments/seed" -H "$ACME" -H "$JSON" -d '[
  { "id": "PAY-1001", "amount": 149.5, "currency": "EUR", "status": "pending" },
  { "id": "PAY-1002", "amount": 89.9,  "currency": "USD", "status": "settled" },
  { "id": "PAY-1003", "amount": 1200,  "currency": "EUR", "status": "authorized" }
]' | jq -c .

say "Seed acme-pay: sandbox API key for the CLI beats (quota 5/hour)"
KEY=$(curl -sf -X POST "$MAIN/__admin/apikeys" -H "$ACME" -H "$JSON" \
  -d '{ "name": "ci-pipeline", "quotaPerHour": 5 }' | jq -r .key)
printf '%s' "$KEY" > "$(dirname "$0")/.demo-key"
echo "key saved to demo/.demo-key ($(cut -c1-8 "$(dirname "$0")/.demo-key")…)"

say "Seed upstream (:9090, tenant globex): the 'real' Globex billing API"
curl -sf -X POST "$UP/__admin/mappings/reset" -H "$GLOBEX" > /dev/null
curl -sf -X POST "$UP/__admin/mappings/import" -H "$GLOBEX" -H "$JSON" -d '{ "mappings": [
  { "name": "Upstream — invoice detail",
    "request": { "method": "GET", "urlPath": "/billing/invoices/INV-2041" },
    "response": { "status": 200, "headers": { "Content-Type": "application/json" },
      "body": "{\"id\":\"INV-2041\",\"amount\":420.0,\"currency\":\"EUR\",\"status\":\"paid\",\"customer\":\"Globex Retail\"}" } },
  { "name": "Upstream — invoice list",
    "request": { "method": "GET", "urlPath": "/billing/invoices" },
    "response": { "status": 200, "headers": { "Content-Type": "application/json" },
      "body": "{\"items\":[{\"id\":\"INV-2041\",\"amount\":420.0,\"currency\":\"EUR\",\"status\":\"paid\"}],\"total\":1}" } }
] }' | jq -c .

say "Done. Dashboard: $MAIN/__mockifyr/  (tenant switcher -> Acme Payments)"
