#!/bin/sh
# PostToolUse hook: whitespace-format the .cs file the agent just touched.
# Cosmetic and fast; NEVER blocks (always exits 0) — the hard gate is stop-gate.sh.
command -v python3 >/dev/null 2>&1 || exit 0
cd "${CLAUDE_PROJECT_DIR:-.}" 2>/dev/null || exit 0
# dotnet format silently ignores ABSOLUTE --include paths — relativize or bust.
FILE=$(python3 -c 'import json,os,sys; p=json.load(sys.stdin).get("tool_input",{}).get("file_path",""); print(os.path.relpath(p) if p else "")' 2>/dev/null) || exit 0
case "$FILE" in
  *.cs) ;;
  *) exit 0 ;;
esac
[ -f "$FILE" ] || exit 0
dotnet format whitespace --include "$FILE" --no-restore >/dev/null 2>&1
exit 0
