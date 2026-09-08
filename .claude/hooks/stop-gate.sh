#!/bin/sh
# Stop hook, MAINTAINER shape (goldpath delivery-cycle RFC, D4), adapted for Mockifyr.
#
# The application-shaped gate builds the whole app and runs specdrift against its manifest.
# Neither fits here: this repository has no manifest — it is the accelerator, not a generated
# app — and a full 23-package solution build on every turn end is slow enough that the hook
# would be deleted. A deleted gate is worse than an absent one, because it is evidence the
# discipline does not work.
#
# So this gate does the two things that are FAST and that catch what actually goes wrong here:
# it builds only the projects whose files changed, and it runs the repository's own ledger and
# documentation gates, which are the checks a change here most often invalidates.
INPUT=$(cat)

case "$INPUT" in
  *'"stop_hook_active":true'* | *'"stop_hook_active": true'*) exit 0 ;;
esac

cd "${CLAUDE_PROJECT_DIR:-.}" || exit 0
command -v git >/dev/null 2>&1 || exit 0
git rev-parse --is-inside-work-tree >/dev/null 2>&1 || exit 0

CHANGED=$(git status --porcelain -- '*.cs' '*.csproj' '*.props' '*.md' '*.json' '*.yaml' '*.yml' '*.sh' 2>/dev/null)
[ -z "$CHANGED" ] && exit 0

LOG=$(mktemp)

# 1. Build only what changed. A touched .cs belongs to the nearest project above it; building
#    that project is seconds, where the solution is minutes.
PROJECTS=$(git status --porcelain -- '*.cs' '*.csproj' '*.props' 2>/dev/null | awk '{print $NF}' | while read -r f; do
  d=$(dirname "$f")
  while [ "$d" != "." ] && [ "$d" != "/" ]; do
    p=$(ls "$d"/*.csproj 2>/dev/null | head -1)
    [ -n "$p" ] && { echo "$p"; break; }
    d=$(dirname "$d")
  done
done | sort -u)

for p in $PROJECTS; do
  if ! dotnet build "$p" --nologo -v quiet >"$LOG" 2>&1; then
    echo "stop-gate: $p does not build — fix it before ending the turn." >&2
    tail -n 30 "$LOG" >&2
    rm -f "$LOG"
    exit 2
  fi
done

# 2. The repository's own gates, in place of the drift check an app would run. These are the
#    ones a change here invalidates most often, and they take about a second each.
for gate in docs-guard kit-freshness; do
  [ -x "scripts/$gate.sh" ] || continue
  if ! "scripts/$gate.sh" >"$LOG" 2>&1; then
    echo "stop-gate: scripts/$gate.sh is red — the ledgers or the docs stopped telling the truth." >&2
    tail -n 20 "$LOG" >&2
    rm -f "$LOG"
    exit 2
  fi
done

rm -f "$LOG"
exit 0
