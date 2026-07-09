#!/usr/bin/env bash
# Package XonoticGodot distributions (T33 — ADR-0014; extended 2026-06 for the full release matrix,
# 2026-07 for the ADR-0015 launcher -core zips).
# Takes the export-preset outputs under dist/<target>/ (produced by `ci/ci.sh --export`, run-release.*,
# or the release workflow), adds licenses + a README + run scripts, and zips each target TWICE:
#   1. a "-core" archive (binary + Godot runtime, NO game data — the launcher payload, ADR-0015 §4;
#      cut BEFORE the assets are laid in, so no archiver-specific exclusion syntax is needed), then
#   2. the "fat" archive (binary + runtime + all Xonotic data — one download, unzip and play),
#      after laying the game assets beside each binary. Mirrors the upstream layout: one install
#      dir = binary + data + launch script.
#
# Targets (each = one export preset → core + fat zips):
#   windows-client    dist/windows-client/XonoticGodot.exe            → XonoticGodot-<ver>-windows-x86_64[-core].zip
#   linux-client      dist/linux-client/XonoticGodot.x86_64           → XonoticGodot-<ver>-linux-x86_64[-core].zip
#   linux-dedicated   dist/linux-dedicated/xonoticgodot-dedicated.*   → XonoticGodot-<ver>-linux-dedicated-x86_64[-core].zip
#   macos-client      dist/macos-client/XonoticGodot.app              → XonoticGodot-<ver>-macos-universal[-core].zip
#       (macOS keeps its data INSIDE the bundle at Contents/Resources/assets/data — DataPaths.Resolve
#        probes ../Resources relative to the executable, so a double-clicked .app finds it.)
#
# Usage:
#   tools/package.sh                          # package every target whose export output exists
#   tools/package.sh windows-client           # only the named target(s)
#   tools/package.sh --version 0.1.0          # stamp the zip names (default: `git describe` or "dev")
#   tools/package.sh --no-zip                 # lay out the dist dirs but skip archiving
#   tools/package.sh --no-music               # forwarded to download-assets.sh (skip the ~300 MB music)
#
# The assets are auto-downloaded (download-assets.sh) if assets/data is missing. Zipping prefers `zip`,
# then `7z`, then python3's zipfile — so it works on the Windows runner (Git Bash has no `zip`) too.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIST="$ROOT/dist"
ASSETS_SRC="$ROOT/assets/data"

info()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33mWARN:\033[0m %s\n' "$*"; }
error() { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; }

# ── args ──────────────────────────────────────────────────────────────────────
no_music_flag=()
do_zip=true
version=""
requested=()
while [ $# -gt 0 ]; do
    case "$1" in
        --no-music) no_music_flag=(--no-music) ;;
        --no-zip)   do_zip=false ;;
        --version)  shift; version="${1:-}" ;;
        --version=*) version="${1#--version=}" ;;
        --help|-h)  grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        --*) echo "Unknown option: $1 (try --help)"; exit 1 ;;
        *) requested+=("$1") ;;
    esac
    shift
done

if [ -z "$version" ]; then
    version="$(git -C "$ROOT" describe --tags --always --dirty 2>/dev/null || echo dev)"
fi
version="${version#v}"   # a "v0.1.0" tag → "0.1.0" in the file name
info "version: $version"

# target → (export-output marker, friendly zip suffix)
marker_for()  { case "$1" in
    windows-client)  echo "windows-client/XonoticGodot.exe" ;;
    linux-client)    echo "linux-client/XonoticGodot.x86_64" ;;
    linux-dedicated) echo "linux-dedicated/xonoticgodot-dedicated.x86_64" ;;
    macos-client)    echo "macos-client/XonoticGodot.app" ;;
esac; }
suffix_for()  { case "$1" in
    windows-client)  echo "windows-x86_64" ;;
    linux-client)    echo "linux-x86_64" ;;
    linux-dedicated) echo "linux-dedicated-x86_64" ;;
    macos-client)    echo "macos-universal" ;;
esac; }

ALL_TARGETS=(windows-client linux-client linux-dedicated macos-client)
[ ${#requested[@]} -gt 0 ] || requested=("${ALL_TARGETS[@]}")

# ── 1. find which requested targets actually have an export output ────────────
targets=()
for t in "${requested[@]}"; do
    marker="$DIST/$(marker_for "$t")"
    if [ -e "$marker" ]; then
        targets+=("$t")
    else
        warn "$t: no export output at $marker — skipping (run the export preset first)"
    fi
done
if [ ${#targets[@]} -eq 0 ]; then
    error "no export outputs found under $DIST/."
    error "run 'ci/ci.sh --export' or the release workflow (needs the Godot 4.6.3 mono export templates) first."
    exit 1
fi
info "packaging: ${targets[*]}"

# ── 2. assets (download if missing — reuses the repo-root downloader) ─────────
if [ ! -d "$ASSETS_SRC" ] || [ -z "$(ls -A "$ASSETS_SRC" 2>/dev/null)" ]; then
    info "assets/data missing — running download-assets.sh ${no_music_flag[*]:-}"
    bash "$ROOT/download-assets.sh" "${no_music_flag[@]}"   # `bash` — exec bit may be absent on a Windows checkout
fi

copy_assets() {  # copy_assets <dest-assets-data-dir>
    local dest="$1"
    info "  assets → ${dest#$DIST/} (excluding the pk3dir .git clones)"
    mkdir -p "$dest"
    if command -v rsync &>/dev/null; then
        rsync -a --delete --exclude='.git' "$ASSETS_SRC/" "$dest/"
    else
        rm -rf "$dest"; mkdir -p "$dest"
        cp -r "$ASSETS_SRC/." "$dest/"
        find "$dest" -name .git -prune -exec rm -rf {} +
    fi
}

write_readme() {  # write_readme <dir> <target>
    local dir="$1" t="$2"
    cat > "$dir/README.txt" <<EOF
Vortex Arena — $t ($version)
A fork of Xonotic, reborn on Godot + C#.  https://github.com/bryankruman/VortexArena

Fat package (no -core in the zip name): binary + Godot runtime + all Xonotic game data —
unzip and play. Keep the files together; the game loads assets/ from beside the binary.
Core package (-core.zip): binary + runtime only, NO game data — for the XonoticGodot
launcher (which supplies a shared data dir via --data), or lay an assets/data tree beside
the binary yourself.
EOF
    case "$t" in
        windows-client)
            cat >> "$dir/README.txt" <<'EOF'

RUN:  double-click XonoticGodot.exe  (or XonoticGodot.console.exe for a debug console window).
EOF
            ;;
        linux-client)
            cat >> "$dir/README.txt" <<'EOF'

RUN:  ./run-client.sh        (or run ./XonoticGodot.x86_64 directly)
EOF
            ;;
        linux-dedicated)
            cat >> "$dir/README.txt" <<'EOF'

RUN:  ./run-dedicated.sh [map]      (headless listen server, e.g. ./run-dedicated.sh stormkeep)
EOF
            ;;
        macos-client)
            cat >> "$dir/README.txt" <<'EOF'

RUN:  double-click XonoticGodot.app
This build is UNSIGNED. The first launch macOS will refuse it ("can't be opened"). Clear the
quarantine flag once, from Terminal in this folder:
    xattr -dr com.apple.quarantine XonoticGodot.app
then double-click it (or right-click → Open).
EOF
            ;;
    esac
}

# assets_dest <target> — where the game data lives inside the target's dist dir.
assets_dest() { case "$1" in
    # macOS: data lives INSIDE the bundle so a double-clicked .app finds it (exe-relative ../Resources).
    macos-client) echo "XonoticGodot.app/Contents/Resources/assets" ;;
    *)            echo "assets" ;;
esac; }

# ── 3. lay out each target (everything EXCEPT the game data) ──────────────────
# The data is laid in AFTER the -core zips are cut (§4): zipping core before assets exist avoids
# archiver-specific exclusion syntax across the zip/7z/python fallbacks (ADR-0015 §4). A previous
# run's laid-in assets are removed first so a re-run over an existing dist/ still cuts a clean
# core zip (costs a full asset re-copy on local re-runs; CI dists are always fresh).
for t in "${targets[@]}"; do
    info "$t:"
    tdir="$DIST/$t"
    rm -rf "$tdir/$(assets_dest "$t")"

    for lic in COPYING GPL-3; do
        [ -f "$ROOT/$lic" ] && cp "$ROOT/$lic" "$tdir/"
    done
    write_readme "$tdir" "$t"

    case "$t" in
        linux-client)
            cp "$ROOT/tools/run-client.sh" "$tdir/"
            chmod +x "$tdir/run-client.sh" "$tdir/XonoticGodot.x86_64" 2>/dev/null || true ;;
        linux-dedicated)
            cp "$ROOT/tools/run-dedicated.sh" "$tdir/"
            chmod +x "$tdir/run-dedicated.sh" "$tdir/xonoticgodot-dedicated.x86_64" 2>/dev/null || true ;;
    esac
done

# ── 4. zip (zip → 7z → python3 fallback, so the Windows runner works too) ─────
zip_dir() {  # zip_dir <out.zip> <dist-relative-dir>
    local out="$1" d="$2"
    rm -f "$out"
    if command -v zip &>/dev/null; then
        # -y: store symlinks AS symlinks (matters for the macOS .app's embedded-framework links).
        ( cd "$DIST" && zip -qry -y "$out" "$d" )
    elif command -v 7z &>/dev/null; then
        ( cd "$DIST" && 7z a -tzip -bso0 -bsp0 "$out" "$d" >/dev/null )
    elif command -v python3 &>/dev/null || command -v python &>/dev/null; then
        local py; py="$(command -v python3 || command -v python)"
        ( cd "$DIST" && "$py" - "$out" "$d" <<'PY'
import sys, os, zipfile
out, root = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=1) as z:
    for dp, _, fs in os.walk(root):
        for f in fs:
            full = os.path.join(dp, f)
            z.write(full, full)
PY
        )
    else
        error "no zip / 7z / python available — '$d' laid out but not archived"; return 1
    fi
}

checksum() {  # checksum <zip-basename>  (sha256sum on Linux, shasum on macOS)
    if command -v sha256sum &>/dev/null; then
        ( cd "$DIST" && sha256sum "$1" >> "SHA256SUMS-$version.txt" )
    elif command -v shasum &>/dev/null; then
        ( cd "$DIST" && shasum -a 256 "$1" >> "SHA256SUMS-$version.txt" )
    fi
}

$do_zip && : > "$DIST/SHA256SUMS-$version.txt"

# ── 4a. -core zips: binary + runtime, NO game data (the assets aren't laid in yet) ─
if $do_zip; then
    for t in "${targets[@]}"; do
        out="$DIST/XonoticGodot-$version-$(suffix_for "$t")-core.zip"
        info "zipping $(basename "$out") (no game data — ADR-0015 launcher payload)"
        zip_dir "$out" "$t"
        checksum "$(basename "$out")"
    done
fi

# ── 4b. lay the game data in (this is also the final --no-zip dist layout) ────
for t in "${targets[@]}"; do
    copy_assets "$DIST/$t/$(assets_dest "$t")/data"
done

# ── 4c. fat zips: binary + runtime + all game data ────────────────────────────
if $do_zip; then
    for t in "${targets[@]}"; do
        out="$DIST/XonoticGodot-$version-$(suffix_for "$t").zip"
        info "zipping $(basename "$out")"
        zip_dir "$out" "$t"
        checksum "$(basename "$out")"
    done
fi

info "Done. Distributions in $DIST/"
$do_zip && info "Zips: $(cd "$DIST" && ls XonoticGodot-"$version"-*.zip 2>/dev/null | tr '\n' ' ')"
