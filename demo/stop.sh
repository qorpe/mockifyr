#!/usr/bin/env bash
# Stops everything the demo started (main host, upstream, runner).
PIDS=$( { lsof -ti tcp:8080; lsof -ti tcp:9090; lsof -ti tcp:7788; } 2>/dev/null | sort -u )
if [ -n "$PIDS" ]; then
  echo "$PIDS" | xargs kill 2>/dev/null
  echo "durduruldu: 8080 + 9090 + 7788"
else
  echo "zaten kapalı."
fi
