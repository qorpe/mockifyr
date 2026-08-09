#!/usr/bin/env bash
# ONE command to bring the whole demo up — works on a fresh clone too:
#   ./demo/start.sh          normal demo  (open admin, seeded stage, Acts 1-8)
#   ./demo/start.sh sso      SSO showcase (Keycloak + OIDC engine — login/lock demo, no seed:
#                            the admin API is locked in this mode, and the SSO act needs no stage)
set -euo pipefail
cd "$(dirname "$0")/.."
MODE="${1:-}"

say()  { printf '\033[1;36m%s\033[0m\n' "$*"; }
ok()   { printf '  \033[1;32m✓ %s\033[0m\n' "$*"; }
fail() { printf '  \033[1;31m✗ %s\033[0m\n' "$*"; exit 1; }

# ── prerequisites ────────────────────────────────────────────────────────────
command -v dotnet  > /dev/null || fail "dotnet bulunamadı — .NET 10 SDK gerekli (global.json)"
command -v jq      > /dev/null || fail "jq bulunamadı — kur: brew install jq"
command -v python3 > /dev/null || fail "python3 bulunamadı"
command -v node    > /dev/null || fail "node bulunamadı — WebSocket adımı için gerekli"
command -v grpcurl > /dev/null || printf '  \033[1;33m! grpcurl yok — gRPC adımı çalışmaz (brew install grpcurl)\033[0m\n'

# ── first-run setup (fresh clone) ────────────────────────────────────────────
if [ ! -f ui/dist/index.html ]; then
  say "Dashboard derleniyor (ilk kurulum, ~1 dk)…"
  command -v pnpm > /dev/null || fail "pnpm bulunamadı — kur: npm i -g pnpm"
  pnpm --dir ui install --silent && pnpm --dir ui build:embedded > /dev/null
  ok "ui/dist hazır"
fi
if [ ! -d demo/node_modules/ws ]; then
  say "Demo bağımlılığı kuruluyor (ws)…"
  command -v pnpm > /dev/null || fail "pnpm bulunamadı — kur: npm i -g pnpm"
  (cd demo && pnpm install --silent)
  ok "ws hazır"
fi

# ── port sanity: if busy, is it OURS? ────────────────────────────────────────
port_check() { # port healthUrl name
  if lsof -ti tcp:"$1" > /dev/null 2>&1; then
    if curl -sf -m 2 "$2" > /dev/null 2>&1; then
      ok "$3 zaten ayakta (:$1)"; return 0
    else
      fail ":$1 portu BAŞKA bir uygulama tarafından kullanılıyor — onu kapat ya da bu demoyu farklı portla çalıştır"
    fi
  fi
  return 1
}

wait_http() { # url name tries logname
  local i=0
  until curl -sf -m 2 "$1" > /dev/null 2>&1; do
    i=$((i+1)); [ "$i" -ge "${3:-60}" ] && fail "$2 ayağa kalkmadı — demo/.$4.log'a bak"
    sleep 1
  done
  ok "$2"
}

if [ "$MODE" = "sso" ]; then
  say "SSO modu başlatılıyor (Keycloak + OIDC'li motor + upstream + runner)…"
  command -v docker > /dev/null || fail "docker bulunamadı — Keycloak için gerekli"
  ./demo/oidc/start-keycloak.sh | sed 's/^/  /'
  # the 8080 engine must be the OIDC one — replace whatever runs there
  if lsof -ti tcp:8080 > /dev/null 2>&1; then lsof -ti tcp:8080 | xargs kill; sleep 2; fi
  nohup ./demo/oidc/run-server-oidc.sh > demo/.server-oidc.log 2>&1 &
  port_check 9090 "http://localhost:9090/__admin/health" "upstream" || nohup ./demo/run-upstream.sh > demo/.upstream.log 2>&1 &
  port_check 7788 "http://localhost:7788/health"         "runner"   || nohup python3 demo/runner.py > demo/.runner.log   2>&1 &
  wait_http "http://localhost:8080/__admin/health" "OIDC'li motor (:8080)" 90 server-oidc
  wait_http "http://localhost:9090/__admin/health" "upstream (:9090)"      90 upstream
  wait_http "http://localhost:7788/health"         "runner (:7788)"        20 runner

  # marker: demo.sh/seed.sh fetch a bearer token per run and attach it to /__admin calls —
  # so the WHOLE flow works even with the admin surface locked.
  touch demo/.sso
  say "Sahne kuruluyor (seed, token'lı)…"
  if ! ./demo/seed.sh > /dev/null 2>&1; then sleep 3; ./demo/seed.sh > /dev/null; fi
  ok "seed tamam — akışın tamamı SSO modunda da çalışır"

  say "Hazır — giriş gösterisi:"
  echo "  1  Dashboard'ı aç: http://localhost:8080/__mockifyr  → 'Sign in with your identity provider'"
  echo "  2  Keycloak sayfasında giriş: demo / demo123"
  echo "  3  Geri dönünce oturum açık; token'sız curl /__admin → 401"
  echo "  4  Tenant'ı Globex'e çevir → kilit; Acme Pay'e dön → veri"
  echo "  Demo ekranı : http://localhost:7788   (adımlar token'ı kendisi taşır)"
  echo "  Normale dönüş: ./demo/stop.sh && ./demo/start.sh"
  open "http://localhost:8080/__mockifyr" 2>/dev/null || true
  exit 0
fi

rm -f demo/.sso
say "Mockifyr demo başlatılıyor…"
port_check 8080 "http://localhost:8080/__admin/health" "ana host" || nohup ./demo/run-server.sh   > demo/.server.log   2>&1 &
port_check 9090 "http://localhost:9090/__admin/health" "upstream" || nohup ./demo/run-upstream.sh > demo/.upstream.log 2>&1 &
port_check 7788 "http://localhost:7788/health"         "runner"   || nohup python3 demo/runner.py > demo/.runner.log   2>&1 &

wait_http "http://localhost:8080/__admin/health" "ana host (:8080)"  90 server
wait_http "http://localhost:9090/__admin/health" "upstream (:9090)"  90 upstream
wait_http "http://localhost:7788/health"         "runner (:7788)"    20 runner

say "Sahne kuruluyor (seed)…"
# The very first admin write after a cold boot can race the host's warm-up — one retry.
if ! ./demo/seed.sh > /dev/null 2>&1; then sleep 3; ./demo/seed.sh > /dev/null; fi
ok "seed tamam — 8 stub, temiz journal/inbox, yeni key"

say "Hazır."
echo "  Demo ekranı : http://localhost:7788"
echo "  Dashboard   : http://localhost:8080/__mockifyr  (tenant: Acme Pay)"
echo "  SSO modu    : ./demo/start.sh sso"
echo "  Durdurmak   : ./demo/stop.sh  (ya da sayfadaki ■ STOP düğmesi)"
open "http://localhost:7788" 2>/dev/null || true
