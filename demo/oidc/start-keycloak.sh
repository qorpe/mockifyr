#!/usr/bin/env bash
# Starts Keycloak in Docker (dev mode) with the mockifyr realm imported.
#   Admin console : http://localhost:8180  (admin / admin)
#   Demo user     : demo / demo123   (tenant claim: acme-pay)
set -euo pipefail
cd "$(dirname "$0")"

if docker ps --format '{{.Names}}' | grep -q '^mockifyr-keycloak$'; then
  echo "keycloak zaten ayakta → http://localhost:8180"
  exit 0
fi
docker rm -f mockifyr-keycloak > /dev/null 2>&1 || true

docker run -d --name mockifyr-keycloak \
  -p 8180:8080 \
  -e KC_BOOTSTRAP_ADMIN_USERNAME=admin \
  -e KC_BOOTSTRAP_ADMIN_PASSWORD=admin \
  -v "$PWD/realm-mockifyr.json:/opt/keycloak/data/import/realm-mockifyr.json:ro" \
  quay.io/keycloak/keycloak:26.0 start-dev --import-realm > /dev/null

printf 'keycloak açılıyor'
for i in $(seq 1 90); do
  if curl -sf -m 2 "http://localhost:8180/realms/mockifyr/.well-known/openid-configuration" > /dev/null 2>&1; then
    echo; echo "hazır → http://localhost:8180  (admin/admin · demo kullanıcı: demo/demo123)"
    exit 0
  fi
  printf '.'; sleep 2
done
echo; echo "keycloak ayağa kalkmadı — docker logs mockifyr-keycloak"; exit 1
