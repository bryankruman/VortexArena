#!/usr/bin/env bash
# Local CI mirror for XonoticGodot (T33 — ADR-0014). Runs the same gate as
# .github/workflows/ci.yml PLUS the asset-dependent steps GitHub can't run:
# with assets/data mounted, the ~18 real-data test classes actually execute
# (in CI they self-skip), and the headless boot smoke exercises real asset
# loading — so THIS script, not the green Actions badge, is the authoritative
# pre-push gate.
#
# Usage:
#   ci/ci.sh                 # build libs+tests, run the suite, build the Godot host, headless smoke
#   ci/ci.sh --no-smoke      # skip the Godot headless boot (no Godot install needed)
#   ci/ci.sh --export        # additionally run both export presets (needs export templates installed)
#
# Env:
#   GODOT  — path to the Godot 4.6.3 mono CONSOLE executable. Defaults to the known dev-machine path.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GODOT="${GODOT:-/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe}"

do_smoke=true
do_export=false
for arg in "$@"; do
    case "$arg" in
        --no-smoke) do_smoke=false ;;
        --export)   do_export=true ;;
        --help|-h)  grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $arg (try --help)"; exit 1 ;;
    esac
done

step()  { printf '\n\033[1;34m== %s ==\033[0m\n' "$*"; }
fail()  { printf '\033[1;31mFAIL:\033[0m %s\n' "$*" >&2; exit 1; }

# ── 1. libraries + tests build (plain .NET SDK, no Godot) ─────────────────────
step "build libraries + tests"
dotnet build "$ROOT/tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj" -c Debug --nologo

# ── 2. the full test suite (assets present → real-data tests run too) ─────────
step "dotnet test (baseline: 1159+ passed / 0 failed; real-data tests skip without assets/data)"
dotnet test "$ROOT/tests/XonoticGodot.Tests/XonoticGodot.Tests.csproj" -c Debug --no-build --nologo
[ -d "$ROOT/assets/data" ] || echo "NOTE: assets/data missing — the ~18 real-data test classes self-skipped (run download-assets.sh for full coverage)."

# ── 3. the Godot host project (restores Godot.NET.Sdk via nuget.config) ───────
step "build the Godot host (XonoticGodot.csproj)"
dotnet build "$ROOT/XonoticGodot.csproj" -c Debug --nologo

# ── 4. headless boot smoke (RUNNING.md 'Run headless') ────────────────────────
if $do_smoke; then
    if [ -x "$GODOT" ] || [ -f "$GODOT" ]; then
        step "headless smoke (--quit-after 200)"
        log="$(mktemp)"
        timeout 180 "$GODOT" --headless --path "$ROOT" --quit-after 200 > "$log" 2>&1 || true
        hard_errors=$(grep -cE '^ERROR:|SCRIPT ERROR|Unhandled exception' "$log" || true)
        echo "hard errors: $hard_errors | warnings: $(grep -c 'WARNING:' "$log" || true)"
        grep -iE "XonoticGodot boot|GameDemo\]|loaded .* shaders|collision brushes|spawned" "$log" || true
        [ "${hard_errors:-1}" -eq 0 ] || { echo "--- $log ---"; tail -40 "$log"; fail "headless smoke had $hard_errors hard error(s)"; }
        rm -f "$log"
    else
        echo "NOTE: Godot not found at '$GODOT' — skipping the headless smoke (set GODOT= or pass --no-smoke to silence)."
    fi
fi

# ── 5. optional: the two export presets (untested path — see ADR-0014) ────────
if $do_export; then
    [ -f "$GODOT" ] || fail "--export needs Godot (set GODOT=)"
    step "export windows-client + linux-dedicated"
    mkdir -p "$ROOT/dist/windows-client" "$ROOT/dist/linux-dedicated"
    "$GODOT" --headless --path "$ROOT" --export-release "windows-client" "$ROOT/dist/windows-client/XonoticGodot.exe"
    "$GODOT" --headless --path "$ROOT" --export-release "linux-dedicated" "$ROOT/dist/linux-dedicated/xonoticgodot-dedicated.x86_64"
    echo "exports in $ROOT/dist/ — run tools/package.sh to bundle assets + zip"
fi

printf '\n\033[1;32mci.sh: all steps passed.\033[0m\n'
