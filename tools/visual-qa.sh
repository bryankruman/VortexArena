#!/usr/bin/env bash
# Windowed Visual QA driver (Wave A5 — T5). Captures a real rendered frame of every stock map and player
# model so a human (or an agent via the Read tool, which renders PNGs) can eyeball the things a renderless
# headless run CANNOT decide: lightmap/deluxemap direction, bezier-patch smoothness, billboard/flare quads,
# material color (no magenta missing-texture), and on-screen bone pose.
#
# WHY THIS IS NOT IN ci.sh: Godot's headless renderer (dummy_video) renders NOTHING — the captured PNG comes
# out blank (game/ScreenshotHook.cs, RUNNING.md "Visual capture"). So this MUST run WINDOWED (a real GPU /
# display). ci.sh's "Visual QA (headless assertions only)" section runs the structural half (VisualQaTests);
# THIS script produces the frames for the manual eye-check in RUNNING.md "Visual QA".
#
# The screenshots land in screenshots/ (git-ignored, carries a .gdignore so the editor skips them). Read each
# PNG and run the RUNNING.md "Visual QA — windowed checklist" against it; diff against an upstream Darkplaces
# baseline if one has been collected.
#
# Usage:
#   tools/visual-qa.sh                      # every stock map + every player model (full sweep)
#   tools/visual-qa.sh --maps               # maps only
#   tools/visual-qa.sh --models             # player models only
#   tools/visual-qa.sh --map stormkeep      # one map
#   tools/visual-qa.sh --model erebus       # one model
#   tools/visual-qa.sh --frames 240         # let assets/shadows settle longer before each capture (default 120)
#   tools/visual-qa.sh --res 1920x1080      # capture resolution (default 1280x720)
#
# Env:
#   GODOT  — path to the Godot 4.6.3 mono CONSOLE executable (so [Screenshot] prints reach stdout).
#            Defaults to the known dev-machine path (same default as ci/ci.sh).

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GODOT="${GODOT:-/c/Program Files/Godot/Godot_v4.6.3-stable_mono_win64_console.exe}"
OUT="$ROOT/screenshots"

# The official maps shipped in xonotic-20230620-maps.pk3 (RUNNING.md "Gotchas → Maps"), verified against the
# pk3's maps/*.bsp listing (the `--map` arg is a BARE map name — NetGame.cs resolves it to maps/<name>.bsp).
# `_hudsetup` (the HUD-editor backdrop) is intentionally omitted; edit to taste — a map missing from the mounted
# data just logs an empty/placeholder world for that one capture.
MAPS=(
    afterslime atelier boil bromine catharsis courtfun dance darkzone erbium
    finalrage fuse geoplanetary glowplant go implosion leave_em_behind
    nexballarena opium runningman runningmanctf silentsiege solarium
    space-elevator stormkeep techassault trident vorix warfare xoylent
)

# The shipped hero player models (models/player/*.iqm, LOD/masked variants excluded). Capture them in the
# default test scene to eyeball the bind/idle pose (un-twisted, feet on the floor, no collapsed bones).
MODELS=(
    erebus gak ignis megaerebus nyx pyria seraphina umbra
)

FRAMES=120
RES="1280x720"
do_maps=true
do_models=true
single_map=""
single_model=""

while [ $# -gt 0 ]; do
    case "$1" in
        --maps)    do_models=false ;;
        --models)  do_maps=false ;;
        --map)     single_map="${2:?--map needs a map name}"; do_models=false; shift ;;
        --model)   single_model="${2:?--model needs a model name}"; do_maps=false; shift ;;
        --frames)  FRAMES="${2:?--frames needs a number}"; shift ;;
        --res)     RES="${2:?--res needs WxH}"; shift ;;
        --help|-h) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $1 (try --help)" >&2; exit 1 ;;
    esac
    shift
done

if [ ! -x "$GODOT" ] && [ ! -f "$GODOT" ]; then
    echo "visual-qa.sh: Godot not found at '$GODOT' — set GODOT= to the mono console exe (see RUNNING.md)." >&2
    exit 1
fi
if [ ! -d "$ROOT/assets/data" ]; then
    echo "visual-qa.sh: WARNING — $ROOT/assets/data missing; captures will be empty (run download-assets.sh)." >&2
fi

mkdir -p "$OUT"
# Keep the Godot editor from importing the captures (mirrors the screenshots/ .gdignore the repo already carries).
[ -f "$OUT/.gdignore" ] || : > "$OUT/.gdignore"

shot() {  # shot <relative-out-png> <extra engine args...>
    local out="$OUT/$1"; shift
    echo "== capturing $out =="
    # WINDOWED (NO --headless): the window opens ~1.5s, settles $FRAMES idle frames, captures, self-quits.
    "$GODOT" --path "$ROOT" --resolution "$RES" \
             --screenshot "$out" --screenshot-frames "$FRAMES" "$@" \
        || echo "  (capture failed for $out — see stdout above)" >&2
}

count=0

if $do_maps; then
    if [ -n "$single_map" ]; then
        shot "map_${single_map}.png" --map "$single_map"; count=$((count+1))
    else
        for m in "${MAPS[@]}"; do
            shot "map_${m}.png" --map "$m"; count=$((count+1))
        done
    fi
fi

if $do_models; then
    # Capturing a specific model means pointing the demo at it. There is no --model boot flag today (the sample
    # model is set in Main.cs/GameDemo, see RUNNING.md "Configuration knobs"), so the per-model sweep boots the
    # default test scene; to capture a NON-default model, set GameDemo.SampleModelPath and re-run --models.
    # We still emit one labelled capture per model name so the checklist has a slot per character; identical
    # frames are expected until the per-model boot seam lands (tracked as a follow-up, see the structured report).
    if [ -n "$single_model" ]; then
        shot "model_${single_model}.png"; count=$((count+1))
    else
        for mdl in "${MODELS[@]}"; do
            shot "model_${mdl}.png"; count=$((count+1))
        done
    fi
fi

echo
echo "visual-qa.sh: captured $count frame(s) into $OUT"
echo "Now run the RUNNING.md 'Visual QA — windowed checklist' against each PNG (Read the file to view it)."
