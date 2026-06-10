#!/usr/bin/env bash
# Package XonoticGodot distributions (T33 — ADR-0014): take the two export-preset outputs
# (dist/windows-client/, dist/linux-dedicated/ — produced by `ci/ci.sh --export` or the CI export
# job), lay the game assets BESIDE each binary (the exported build mounts `assets/data` relative to
# its own directory — the CWD contract, GameDemo.ResolveDataPath), add the run script + licenses,
# and zip both. Mirrors the upstream layout: one install dir = binary + data + launch script
# (Base/Makefile two-binary topology + Base/xonotic-linux-sdl.sh).
#
# Usage:
#   tools/package.sh                 # package both targets (downloads assets if missing)
#   tools/package.sh --no-music     # passed through to download-assets.sh
#   tools/package.sh --no-zip       # lay out the dist dirs but skip zipping
#
# Prereqs: the exports must exist (run `ci/ci.sh --export` first — needs Godot + export templates).

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIST="$ROOT/dist"
ASSETS_SRC="$ROOT/assets/data"

info()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
error() { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; }

no_music_flag=()
do_zip=true
for arg in "$@"; do
    case "$arg" in
        --no-music) no_music_flag=(--no-music) ;;
        --no-zip)   do_zip=false ;;
        --help|-h)  grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $arg (try --help)"; exit 1 ;;
    esac
done

# ── 1. the export outputs (produced by the export presets) ────────────────────
targets=()
[ -f "$DIST/windows-client/XonoticGodot.exe" ] && targets+=(windows-client)
[ -f "$DIST/linux-dedicated/xonoticgodot-dedicated.x86_64" ] && targets+=(linux-dedicated)
if [ ${#targets[@]} -eq 0 ]; then
    error "no export outputs found under $DIST/."
    error "run 'ci/ci.sh --export' (needs the Godot 4.6.3 mono export templates) first."
    exit 1
fi
info "packaging: ${targets[*]}"

# ── 2. assets (download if missing — reuses the repo-root downloader) ─────────
if [ ! -d "$ASSETS_SRC" ] || [ -z "$(ls -A "$ASSETS_SRC" 2>/dev/null)" ]; then
    info "assets/data missing — running download-assets.sh ${no_music_flag[*]:-}"
    "$ROOT/download-assets.sh" "${no_music_flag[@]}"
fi

# ── 3. lay out each target: assets beside the binary + run script + licenses ──
copy_assets() {
    local dest="$1/assets/data"
    info "  assets → $dest (excluding the pk3dir .git clones)"
    mkdir -p "$dest"
    if command -v rsync &>/dev/null; then
        rsync -a --delete --exclude='.git' "$ASSETS_SRC/" "$dest/"
    else
        rm -rf "$dest"
        mkdir -p "$dest"
        cp -r "$ASSETS_SRC/." "$dest/"
        find "$dest" -name .git -prune -exec rm -rf {} +
    fi
}

for t in "${targets[@]}"; do
    info "$t:"
    copy_assets "$DIST/$t"
    for lic in COPYING GPL-3; do
        [ -f "$ROOT/$lic" ] && cp "$ROOT/$lic" "$DIST/$t/"
    done
    if [ "$t" = linux-dedicated ]; then
        cp "$ROOT/tools/run-dedicated.sh" "$DIST/$t/"
        chmod +x "$DIST/$t/run-dedicated.sh" "$DIST/$t/xonoticgodot-dedicated.x86_64" 2>/dev/null || true
    fi
done

# ── 4. zip ────────────────────────────────────────────────────────────────────
if $do_zip; then
    if command -v zip &>/dev/null; then
        for t in "${targets[@]}"; do
            out="$DIST/XonoticGodot-$t.zip"
            info "zipping $out"
            rm -f "$out"
            (cd "$DIST" && zip -qry "$out" "$t")
        done
    else
        error "zip not found — dist dirs are laid out but not archived (--no-zip to silence)"
    fi
fi

info "Done. Distributions in $DIST/"
info "Windows client: launch XonoticGodot.exe FROM ITS OWN DIRECTORY (assets resolve CWD-relative)."
info "Linux dedicated: ./run-dedicated.sh [map]   (cd's itself — symlinks/.desktop entries are fine)."
