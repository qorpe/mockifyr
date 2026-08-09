#!/usr/bin/env bash
# The demo host with OIDC admin auth (Keycloak) — replaces run-server.sh for the SSO act.
# Prereq: ./demo/oidc/start-keycloak.sh   (issuer http://localhost:8180/realms/mockifyr)
#
# What this shows: the dashboard's login switches to SSO (authorization code + PKCE);
# the token's "tenant" claim scopes the signed-in user to acme-pay, exactly like a
# tenant credential; every admin change is audited as oidc:demo.
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$PWD"
exec dotnet run --project src/Mockifyr.Server -c Release --no-build -- \
  --port 8080 \
  --https-port 8443 \
  --root-dir "$ROOT/demo/work" \
  --dashboard "$ROOT/ui/dist" \
  --sms-profile twilio \
  --smtp-port 2525 \
  --sandbox-auth=true \
  --audit=true \
  --oidc-authority http://localhost:8180/realms/mockifyr \
  --oidc-client-id mockifyr-dashboard \
  --oidc-tenant-claim tenant
