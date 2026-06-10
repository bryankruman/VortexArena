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
"$GODOT" --headless --path "$PROJ" --export-release "$PRESET" "$OUT"

echo "[run-release] launching: $OUT $*"
"$OUT" "$@"
