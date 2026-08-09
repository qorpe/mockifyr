#!/usr/bin/env bash
# The live-demo step runner. Each beat is one named step: `./demo/demo.sh <step>`
# Steps print the command they run, then run it. `./demo/demo.sh` lists all steps.
set -euo pipefail
cd "$(dirname "$0")/.."

# Overridable targets — point the same steps at ANY Mockifyr (e.g. the Docker compose stack):
#   MOCKIFYR_MAIN=http://localhost:8090 MOCKIFYR_UP=http://localhost:9091 \
#   MOCKIFYR_BASIC=admin:demo123 ./demo/demo.sh payments-create
MAIN="${MOCKIFYR_MAIN:-http://localhost:8080}"
UP="${MOCKIFYR_UP:-http://localhost:9090}"
ACME='X-Mockifyr-Tenant: acme-pay'
GLOBEX='X-Mockifyr-Tenant: globex'
JSON='Content-Type: application/json'
KEY_FILE=demo/.demo-key

show() { printf '\033[1;33m$ %s\033[0m\n' "$*"; }
run()  { show "$*"; eval "$*"; }

# SSO mode (demo/.sso marker, created by `start.sh sso`): admin calls need a bearer token —
# exactly what any CI/script does against an OIDC-locked admin API. The wrapper adds the
# token ONLY to /__admin calls; mock-surface calls stay bare (a JWT there would read as an
# invalid sandbox key).
if [ -f demo/.sso ]; then
  SSO_TOKEN=$(./demo/oidc/token.sh 2>/dev/null || true)          # acme-pay identity
  SSO_TOKEN_GLOBEX=$(./demo/oidc/token.sh globex 2>/dev/null || true)  # globex identity
fi
curl() {
  if printf '%s' "$*" | grep -q "__admin"; then
    if [ -n "${SSO_TOKEN:-}" ]; then
      # each tenant is managed by ITS OWN identity — the tenant claim locks tokens to one tenant
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

step="${1:-}"
case "$step" in

# ── Setup helpers ─────────────────────────────────────────────────────────────
wipe)
  # FULL clean: empties every tenant on both hosts — no baseline stubs, nothing.
  # After this the dashboard shows a truly empty platform. Run seed.sh to set the stage again.
  for T in acme-pay globex default; do
    H="X-Mockifyr-Tenant: $T"
    curl -s -X POST "$MAIN/__admin/mappings/reset"  -H "$H" > /dev/null
    curl -s -X DELETE "$MAIN/__admin/requests"      -H "$H" > /dev/null
    curl -s -X POST "$MAIN/__admin/messages/reset"  -H "$H" > /dev/null
    curl -s -X POST "$MAIN/__admin/resources/reset" -H "$H" > /dev/null
    curl -s -X POST "$MAIN/__admin/scenarios/reset" -H "$H" > /dev/null
    curl -s -X DELETE "$MAIN/__admin/clock"         -H "$H" > /dev/null
    curl -s -X DELETE "$MAIN/__admin/degradation"   -H "$H" > /dev/null
    for ID in $(curl -s "$MAIN/__admin/message-mappings" -H "$H" | jq -r '.messageMappings[].id // empty'); do
      curl -s -X DELETE "$MAIN/__admin/message-mappings/$ID" -H "$H" > /dev/null
    done
    for ID in $(curl -s "$MAIN/__admin/apikeys" -H "$H" | jq -r '.keys[].id // empty'); do
      curl -s -X DELETE "$MAIN/__admin/apikeys/$ID" -H "$H" > /dev/null
    done
    curl -s -X POST "$UP/__admin/mappings/reset" -H "$H" > /dev/null 2>&1 || true
    echo "wiped: $T"
  done
  curl -s -X DELETE "$MAIN/__admin/grpc/descriptors/greeter.dsc" > /dev/null 2>&1 || true
  rm -f "$KEY_FILE"
  echo "done — everything is empty. Run ./demo/seed.sh to set the stage again."
  ;;
openapi-import)
  # CLI equivalent of the dashboard's Add stub -> OpenAPI -> Import spec.
  run "curl -s -X POST '$MAIN/__admin/openapi/import?stateful=true' -H '$ACME' --data-binary @demo/specs/payments.yaml"
  echo
  ;;
rehearse)
  # Runs EVERY beat in demo order against a fresh seed — the pre-show green check.
  # Live demo still goes step by step; this only proves the machine is ready.
  ./demo/seed.sh > /dev/null
  echo "seeded."
  STEPS="openapi-import payments-create payments-get payments-list key-quota
         order-ok order-bad near-miss webhook sms otp email
         grpc-descriptor grpc graphql graphql-messy ws scenario
         record-start record-drive record-snapshot record-import drift record-verify record-stop
         token clock-freeze token clock-reset chaos-on chaos-probe chaos-off
         verify-stubs verify-traffic"
  for st in $STEPS; do
    printf '\n\033[1;36m██ %s\033[0m\n' "$st"
    if ! "$0" "$st"; then printf '\033[1;31m✗ %s FAILED — stopping\033[0m\n' "$st"; exit 1; fi
  done
  printf '\n\033[1;32m✓ rehearse complete — every beat green. Re-run ./demo/seed.sh (+ dashboard OpenAPI import) before the real demo.\033[0m\n'
  ;;

# ── Act 1 · sandbox ────────────────────────────────────────────────────────────
payments-create)
  show "curl -si $MAIN/payments -d '{…PAY-2001…}'  (create, then follow the Location header)"
  LOC=$(curl -si "$MAIN/payments" -H "$ACME" -H "$JSON" \
    -d '{"id":"PAY-2001","amount":75,"currency":"GBP","status":"pending"}' \
    | tee /dev/stderr | grep -i '^Location:' | awk '{print $2}' | tr -d '\r')
  echo
  run "curl -s $MAIN$LOC -H '$ACME' | jq ."
  ;;
payments-get)
  run "curl -s $MAIN/payments/PAY-1001 -H '$ACME' | jq ."
  ;;
payments-list)
  run "curl -s $MAIN/payments -H '$ACME' | jq ."
  ;;
key-quota)
  # Uses the seeded ci-pipeline key by default; pass a token to use a live-created one:
  #   ./demo/demo.sh key-quota mfk_xxxxx     (create it with quota 5 for the 6-call beat)
  KEY="${2:-$(cat "$KEY_FILE")}"
  echo "using sandbox key from demo/.demo-key — no tenant header, the key IS the tenant"
  for i in 1 2 3 4 5 6; do
    show "curl -si $MAIN/payments -H 'X-Api-Key: mfk_…' (call $i)"
    curl -si "$MAIN/payments" -H "X-Api-Key: $KEY" | grep -iE '^HTTP|^x-ratelimit|^retry-after' || true
  done
  ;;

# ── Act 2 · matching + near-miss ──────────────────────────────────────────────
order-ok)
  run "curl -s $MAIN/api/orders -H '$ACME' -H '$JSON' -H 'X-Partner-Key: secret' -d '{\"sku\":\"WIDGET-1\",\"qty\":3}' | jq ."
  ;;
order-bad)
  run "curl -si $MAIN/api/orders -H '$ACME' -H '$JSON' -H 'X-Partner-Key: WRONG' -d '{\"sku\":\"WIDGET-1\",\"qty\":3}' | head -3"
  ;;
near-miss)
  run "curl -s -X POST $MAIN/__admin/near-misses/request -H '$ACME' -H '$JSON' -d '{\"method\":\"POST\",\"url\":\"/api/orders\",\"headers\":{\"X-Partner-Key\":\"WRONG\",\"Content-Type\":\"application/json\"},\"body\":\"{\\\"sku\\\":\\\"WIDGET-1\\\",\\\"qty\\\":3}\"}' | jq '.nearMisses[0] | {stubId, distance, attributes}'"
  ;;

# ── Act 3 · callbacks ─────────────────────────────────────────────────────────
webhook)
  run "curl -si $MAIN/api/payments/authorize -H '$ACME' -H '$JSON' -d '{\"paymentId\":\"PAY-1001\"}' | head -3"
  echo; echo "…webhook fires after 2 s; check:"
  sleep 2.8
  run "curl -s '$MAIN/__admin/requests' -H '$ACME' | jq '[.requests[] | {method, url, status}] | .[0:3]'"
  ID=$(curl -s "$MAIN/__admin/requests" -H "$ACME" | jq -r '[.requests[] | select(.url=="/api/payments/authorize")][0].id')
  run "curl -s $MAIN/__admin/requests/$ID -H '$ACME' | jq '.webhooks'"
  ;;

# ── Act 4 · messages ──────────────────────────────────────────────────────────
sms)
  run "curl -s $MAIN/2010-04-01/Accounts/ACdemo/Messages.json -H '$ACME' --data-urlencode 'To=+905551112233' --data-urlencode 'From=+18005550199' --data-urlencode 'Body=Acme Payments: your verification code is 482913' | jq '{sid, status, to, from}'"
  ;;
otp)
  run "curl -s '$MAIN/__admin/messages/otp?channel=sms&recipient=%2B905551112233' -H '$ACME' | jq ."
  ;;
email)
  # The SMTP capture listener binds loopback by design — reachable only from the engine's own
  # host. In the containerized stack that host is the mockifyr container itself, so this beat
  # cannot run from outside it (product issue filed to make the bind address configurable).
  if [ -n "${MOCKIFYR_SMTP_HOST:-}" ] && [ "${MOCKIFYR_SMTP_HOST}" != "localhost" ]; then
    echo "SKIPPED — SMTP listener is loopback-only; not reachable across containers."
    echo "(run the local demo for this beat, or see the filed product issue)"
    exit 0
  fi
  run "python3 demo/send-email.py"
  run "curl -s '$MAIN/__admin/messages?channel=email' -H '$ACME' | jq '[.messages[] | {channel, subject, recipient: .to}] | .[0:2]'"
  ;;

# ── Act 5 · gRPC / GraphQL / WebSocket ────────────────────────────────────────
grpc-descriptor)
  run "curl -s -X POST --data-binary @demo/grpc/greeter.dsc -H '$ACME' '$MAIN/__admin/grpc/descriptors?name=greeter' | jq ."
  run "curl -s -H '$ACME' $MAIN/__admin/grpc/descriptors | jq '.services[0] | {service, methods: [.methods[].method]}'"
  ;;
grpc)
  run "grpcurl -insecure -protoset demo/grpc/greeter.dsc -H 'x-mockifyr-tenant: acme-pay' -d '{\"name\":\"Ada\"}' ${MOCKIFYR_GRPC:-localhost:8443} mockifyr.grpc.test.Greeter/SayHello"
  ;;
graphql)
  run "curl -s $MAIN/graphql -H '$ACME' -H '$JSON' -d '{\"query\":\"query Payment(\$id: ID!) { payment(id: \$id) { id status amount } }\",\"variables\":{\"id\":\"PAY-1001\"},\"operationName\":\"Payment\"}' | jq ."
  ;;
graphql-messy)
  echo "same query — fields reordered, whitespace mangled. AST-normalized matching still hits:"
  run "curl -s $MAIN/graphql -H '$ACME' -H '$JSON' -d '{\"query\":\"query Payment(\$id:ID!){payment(id:\$id){amount status id}}\",\"variables\":{\"id\":\"PAY-1001\"},\"operationName\":\"Payment\"}' | jq ."
  ;;
ws)
  run "node demo/ws-client.mjs"
  ;;

# ── Act 6 · scenarios ─────────────────────────────────────────────────────────
scenario)
  run "curl -s $MAIN/api/payments/PAY-1001/status -H '$ACME' | jq -c ."
  run "curl -s $MAIN/api/payments/PAY-1001/status -H '$ACME' | jq -c ."
  echo "…now open the Scenarios page: the pill sits on 'Settled'; click 'Started' to rewind."
  ;;

# ── Act 7 · record & drift ────────────────────────────────────────────────────
record-start)
  run "curl -s -X POST $MAIN/__admin/recordings/start -H '$GLOBEX' -H '$JSON' -d '{\"targetBaseUrl\":\"$UP\"}' -o /dev/null -w '%{http_code}\n'"
  run "curl -s $MAIN/__admin/recordings/status -H '$GLOBEX' | jq ."
  ;;
record-drive)
  run "curl -s $MAIN/billing/invoices/INV-2041 -H '$GLOBEX' | jq -c ."
  run "curl -s $MAIN/billing/invoices -H '$GLOBEX' | jq -c ."
  ;;
record-snapshot)
  run "curl -s -X POST $MAIN/__admin/recordings/snapshot -H '$GLOBEX' | jq '{captured: (.mappings | length), first: .mappings[0].request}'"
  ;;
record-import)
  echo "(in the dashboard this is the Recordings page's 'Import all' button)"
  run "curl -s -X POST $MAIN/__admin/recordings/snapshot -H '$GLOBEX' | curl -s -X POST $MAIN/__admin/mappings/import -H '$GLOBEX' -H '$JSON' --data-binary @- -o /dev/null -w '%{http_code}\n'"
  run "curl -s $MAIN/__admin/mappings -H '$GLOBEX' | jq '[.mappings[] | {method: .request.method, url: .request.url}]'"
  ;;
drift)
  echo "the 'real' upstream changes: currency disappears, an unannounced field appears…"
  run "curl -s -X POST $UP/__admin/mappings/reset -H '$GLOBEX' > /dev/null"
  run "curl -s -X POST $UP/__admin/mappings/import -H '$GLOBEX' -H '$JSON' -d '{\"mappings\":[{\"request\":{\"method\":\"GET\",\"urlPath\":\"/billing/invoices/INV-2041\"},\"response\":{\"status\":200,\"headers\":{\"Content-Type\":\"application/json\"},\"body\":\"{\\\"id\\\":\\\"INV-2041\\\",\\\"amount\\\":420.0,\\\"status\\\":\\\"paid\\\",\\\"settlementBatch\\\":\\\"B-77\\\"}\"}}]}' | jq -c ."
  ;;
record-verify)
  run "curl -s $MAIN/billing/invoices/INV-2041 -H '$GLOBEX' | jq -c ."
  run "curl -s -X POST $MAIN/__admin/recordings/verify -H '$GLOBEX' | jq ."
  ;;
record-stop)
  run "curl -s -X POST $MAIN/__admin/recordings/stop -H '$GLOBEX' | jq '{captured: (.mappings | length)}'"
  ;;

# ── Act 8 · clock, chaos, conformance ─────────────────────────────────────────
token)
  run "curl -s $MAIN/api/token -H '$ACME' | jq ."
  ;;
clock-freeze)
  run "curl -s -X PUT $MAIN/__admin/clock -H '$ACME' -H '$JSON' -d '{\"frozenAt\":\"2027-01-01T09:00:00Z\"}' | jq ."
  ;;
clock-reset)
  run "curl -s -X DELETE $MAIN/__admin/clock -H '$ACME' -o /dev/null -w '%{http_code}\n'"
  ;;
chaos-on)
  run "curl -s -X PUT $MAIN/__admin/degradation -H '$ACME' -H '$JSON' -d '{\"latency\":{\"fixedMs\":300,\"jitterMs\":200},\"errorRate\":{\"ratio\":0.4,\"status\":503},\"seed\":42}' | jq ."
  ;;
chaos-probe)
  for i in 1 2 3 4 5; do
    show "curl /payments (probe $i)"
    curl -s -o /dev/null -w '  HTTP %{http_code} in %{time_total}s\n' "$MAIN/payments" -H "$ACME"
  done
  ;;
chaos-off)
  run "curl -s -X DELETE $MAIN/__admin/degradation -H '$ACME' -o /dev/null -w '%{http_code}\n'"
  ;;
verify-stubs)
  run "curl -s -X POST $MAIN/__admin/openapi/verify -H '$ACME' --data-binary @demo/specs/payments.yaml | jq '{conforms, operationsInSpec, operationsCovered, findings: [.findings[] | {kind, method, path}]}'"
  ;;
verify-traffic)
  run "curl -s -X POST $MAIN/__admin/requests/verify -H '$ACME' --data-binary @demo/specs/payments.yaml | jq '{conforms, requestsExamined, requestsConforming, findings: [.findings[] | {kind, method, url}]}'"
  ;;

*)
  grep -E '^[a-z0-9-]+\)' "$0" | tr -d ')' | sed 's/^/  /'
  ;;
esac
