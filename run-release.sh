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

PROJ="$(cd "$(dirname "$0")" && pwd)"

# Pick the desktop-client export preset + output binary for THIS OS (export_presets.cfg). GODOT may be
# overridden via the environment so non-Windows devs point at their own Godot 4.6.3 mono console build.
is_windows=false
case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*|Windows_NT)
        is_windows=true
        GODOT="${GODOT:-/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe}"
        PRESET="windows-client"; OUT="$PROJ/dist/windows-client/XonoticGodot.exe" ;;   # preset.0
    Linux)
        GODOT="${GODOT:-godot}"
        PRESET="linux-client";   OUT="$PROJ/dist/linux-client/XonoticGodot.x86_64" ;;  # preset.2
    Darwin)
        echo "[run-release] macOS export is CI-only / best-effort (ADR-0014) — use the release workflow." >&2
        exit 1 ;;
    *)  echo "[run-release] unsupported OS '$(uname -s)'" >&2; exit 1 ;;
esac

# On non-Windows the export's C# publish reads nuget.config; drop the Windows-only 'godot-editor' local
# source (a C:\ path absent here) or NuGet/SDK resolution hard-fails. Backed up + restored on exit.
if ! $is_windows && grep -q godot-editor "$PROJ/nuget.config" 2>/dev/null; then
    _nuget_bak="$(mktemp)"; cp "$PROJ/nuget.config" "$_nuget_bak"
    trap 'cp -f "$_nuget_bak" "$PROJ/nuget.config"; rm -f "$_nuget_bak"' EXIT
    dotnet nuget remove source godot-editor --configfile "$PROJ/nuget.config" >/dev/null
fi

mkdir -p "$(dirname "$OUT")"
echo "[run-release] exporting '$PRESET' (release, optimized C#) → $OUT"

# Godot's headless --export-release frequently exits NON-ZERO even on a fully successful export
# (benign import/shader/.NET warnings). So don't trust the exit code — gate on the binary appearing.
set +e
"$GODOT" --headless --path "$PROJ" --export-release "$PRESET" "$OUT"
rc=$?
set -e
if [ ! -e "$OUT" ]; then
    echo "[run-release] export FAILED — '$OUT' was not produced (godot exit $rc)" >&2
    exit 1
fi
[ "$rc" -ne 0 ] && echo "[run-release] note: godot exited $rc but produced the binary (benign export warnings) — continuing."

# Launch from the project root so the exported build's asset resolver finds the in-tree assets/data
# (GameDemo.ResolveDataPath falls back to a CWD-relative 'assets/data' in an exported build; a packaged
# zip instead carries assets beside the binary). Without this a release run boots into an empty world.
echo "[run-release] launching: $OUT $*"
cd "$PROJ"
exec "$OUT" "$@"
