#!/usr/bin/env bash
# Export + launch a TRUE RELEASE build of XonoticGodot — optimized C# (csharp=Release) AND
# godot-context=release, with NO editor/debugger overhead. This is the only way to measure real-world
# performance: running from the Godot editor or a Rider "Player" config ALWAYS loads the Debug assembly and
# reports godot-context=debug, regardless of the Rider build configuration.
#
# ONE-TIME PREREQUISITE: install the export templates (the export_templates dir is currently empty):
#   Godot editor  →  Editor menu  →  Manage Export Templates…  →  Download and Install  (4.6.3 .NET/Mono)
#
# Then just run:  ./run-release.sh            (export + launch)
#                 ./run-release.sh --host atelier --gametype dm   (extra args forwarded to the game)
set -euo pipefail

GODOT="/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe"
PROJ="$(cd "$(dirname "$0")" && pwd)"
PRESET="windows-client"                              # preset.0 in export_presets.cfg
OUT="$PROJ/dist/windows-client/XonoticGodot.exe"

mkdir -p "$(dirname "$OUT")"
echo "[run-release] exporting '$PRESET' (release, optimized C#) → $OUT"

# Godot's headless --export-release frequently exits NON-ZERO even on a fully successful export
# (benign import/shader/.NET warnings). So don't trust the exit code — gate on the binary appearing.
set +e
"$GODOT" --headless --path "$PROJ" --export-release "$PRESET" "$OUT"
rc=$?
set -e
if [ ! -f "$OUT" ]; then
    echo "[run-release] export FAILED — '$OUT' was not produced (godot exit $rc)" >&2
    exit 1
fi
[ "$rc" -ne 0 ] && echo "[run-release] note: godot exited $rc but produced the binary (benign export warnings) — continuing."

# Launch from the project root so the exported build's asset resolver finds the in-tree assets/data
# (DataPaths.Resolve falls back to a CWD-relative 'assets/data' in an exported build; a packaged
# zip instead carries assets beside the binary). Without this a release run boots into an empty world.
echo "[run-release] launching: $OUT $*"
cd "$PROJ"
exec "$OUT" "$@"
