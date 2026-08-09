#!/usr/bin/env bash
# The "real" upstream for the recording act — a second Mockifyr instance on :9090.
# In the story this is Globex's live billing API that we record and then mock.
set -euo pipefail
cd "$(dirname "$0")/.."
exec dotnet run --project src/Mockifyr.Server -c Release --no-build -- \
  --port 9090 \
  --root-dir demo/upstream
