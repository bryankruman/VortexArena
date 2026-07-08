#!/usr/bin/env bash
# perf-run — one-command perf capture + report (bash twin of perf-run.ps1; see PERF-DEBUGGING.md).
# Usage: perf-run.sh <label> <secs> [extra --cvar flags...]
#   perf-run.sh baseline 35
#   perf-run.sh pvs_off  35 --cvar r_pvs_cull 0
# Env: PERF_MAP (default catharsis), PERF_BOTS (default 6), PERF_DEBUG=1 (Godot console binary
# on the project instead of the release export — NOT release-representative).
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LOGDIR="$HOME/XonData/logs"
LABEL="${1:-run}"; SECS="${2:-35}"; shift 2 || true
MAP="${PERF_MAP:-catharsis}"; BOTS="${PERF_BOTS:-6}"

if [ "${PERF_DEBUG:-0}" = "1" ]; then
    EXE="/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe"
    EXTRA_ARGS=(--path "$ROOT")
else
    EXE="$ROOT/dist/windows-client/XonoticGodot.exe"
    EXTRA_ARGS=()
    [ -x "$EXE" ] || { echo "!!! release export missing at $EXE — export windows-client first (or PERF_DEBUG=1)"; exit 1; }
fi

powershell -NoProfile -Command "Get-Process Godot*,XonoticGodot* -ErrorAction SilentlyContinue | Stop-Process -Force" 2>/dev/null
BEFORE=$(ls -t "$LOGDIR"/*.log 2>/dev/null | head -1)
echo ">>> [$LABEL] $MAP + $BOTS bots, ${SECS}s  extra: $*"
timeout $((SECS+60)) "$EXE" "${EXTRA_ARGS[@]}" --host "$MAP" --gametype dm --bots "$BOTS" \
    --cvar cl_frameprofiler 2 --cvar cl_frameprofiler_hitchms 8 \
    "$@" --quit-after-seconds "$SECS" > "$ROOT/_scratch/perf_${LABEL}.out" 2>&1
sleep 2   # session-log writer flush
NEW=$(ls -t "$LOGDIR"/*.log 2>/dev/null | head -1)
if [ "$NEW" = "${BEFORE:-}" ] || [ -z "$NEW" ]; then
    echo "!!! no new session log (boot failed?) — see _scratch/perf_${LABEL}.out"; tail -20 "$ROOT/_scratch/perf_${LABEL}.out"; exit 1
fi
echo ">>> [$LABEL] session: $(basename "$NEW")"
python "$ROOT/tools/perf-report.py" "$NEW" --json "$ROOT/_scratch/perf_${LABEL}.json"
