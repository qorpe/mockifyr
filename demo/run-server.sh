#!/usr/bin/env bash
# Main demo host — engine + admin API + dashboard + messages + sandbox auth + audit.
# Dashboard: http://localhost:8080/__mockifyr/   Admin: http://localhost:8080/__admin
# gRPC (HTTP/2 over TLS, self-signed): https://localhost:8443
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"
exec dotnet run --project src/Mockifyr.Server -c Release -- \
  --port 8080 \
  --https-port 8443 \
  --root-dir "$ROOT/demo/work" \
  --dashboard "$ROOT/ui/dist" \
  --sms-profile twilio \
  --smtp-port 2525 \
  --sandbox-auth=true \
  --audit=true
