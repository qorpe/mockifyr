#!/usr/bin/env bash
# The kit-freshness gate (extraction RFC §5): the dashboard may not silently fall behind
# the published @qorpe/ui. Red when the pinned version is >1 minor or >14 days behind
# the registry's latest — same discipline as the NuGet train-freshness step.
set -euo pipefail
cd "$(dirname "$0")/../ui"
PINNED=$(node -p "require('./package.json').dependencies['@qorpe/ui']")
# Air-gapped runs are a first-class scenario (constitution): no registry, no verdict —
# skip HONESTLY, the same convention as review-agent.yml without its key. Freshness is
# enforced wherever the network exists (hosted CI), never faked where it does not.
if ! LATEST=$(npm view @qorpe/ui version 2>/dev/null) || [ -z "$LATEST" ]; then
  echo "kit-freshness: registry unreachable — skipped honestly (air-gapped run; pinned $PINNED unverified, not stale)."
  exit 0
fi
if [ "$PINNED" = "$LATEST" ]; then
  echo "kit-freshness: pinned $PINNED == latest — fresh."
  exit 0
fi
IFS=. read -r P_MAJ P_MIN _ <<< "$PINNED"
IFS=. read -r L_MAJ L_MIN _ <<< "$LATEST"
PUBLISHED_AT=$(npm view "@qorpe/ui@$LATEST" time --json | node -p "JSON.parse(require('fs').readFileSync(0))['$LATEST']" 2>/dev/null || echo "")
AGE_DAYS=999
if [ -n "$PUBLISHED_AT" ]; then
  AGE_DAYS=$(node -p "Math.floor((Date.now() - new Date('$PUBLISHED_AT').getTime()) / 86400000)")
fi
# Tightened 2026-09-05 (family closure audit, same change as goldpath): one minor, two weeks.
if [ "$L_MAJ" -gt "$P_MAJ" ] || [ $((L_MIN - P_MIN)) -gt 1 ] || { [ "$AGE_DAYS" -gt 14 ] && [ "$LATEST" != "$PINNED" ]; }; then
  echo "kit-freshness: pinned $PINNED is behind latest $LATEST (latest published ${AGE_DAYS}d ago) — update the pin."
  exit 1
fi
echo "kit-freshness: pinned $PINNED, latest $LATEST (${AGE_DAYS}d old) — within tolerance."
