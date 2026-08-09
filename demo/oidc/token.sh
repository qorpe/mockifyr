#!/usr/bin/env bash
# Prints a fresh access token for a demo realm user (password grant).
#   ./token.sh          → demo   (tenant claim: acme-pay)
#   ./token.sh globex   → globex (tenant claim: globex)
# This is what any CI/script does against an OIDC-protected admin API.
set -euo pipefail
USER="${1:-demo}"
case "$USER" in
  demo)   PASS="demo123" ;;
  globex) PASS="globex123" ;;
  *) echo "unknown demo user: $USER" >&2; exit 1 ;;
esac
curl -sf -X POST "http://localhost:8180/realms/mockifyr/protocol/openid-connect/token" \
  -d grant_type=password \
  -d client_id=mockifyr-dashboard \
  -d username="$USER" \
  -d password="$PASS" | jq -r .access_token
