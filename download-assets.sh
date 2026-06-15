#!/usr/bin/env bash
# Download Xonotic game assets into assets/data/ for the XonoticGodot port.
#
# Sources:
#   - xonotic-data.pk3dir   → git clone from gitlab.com/xonotic (runtime assets only)
#   - xonotic-music.pk3dir  → git clone from gitlab.com/xonotic
#   - xonotic-maps.pk3dir   → git clone from gitlab.com/xonotic
#   - font-*.pk3dir          → extracted from the main xonotic.git repo (sparse checkout)
#   - compiled map .pk3s     → downloaded from dl.xonotic.org release zip
#
# Usage (the script lives at the REPO ROOT):
#   ./download-assets.sh                # full download (data + music + maps)
#   ./download-assets.sh --no-music     # skip the ~300 MB music repo
#   ./download-assets.sh --no-maps      # skip the ~750 MB map pk3 download
#   ./download-assets.sh --dest <dir>   # download into <dir> instead of assets/data
#                                       # (tools/package.sh-style reuse for a dist layout)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$SCRIPT_DIR"
ASSETS_DIR="$REPO_ROOT/assets/data"

GITLAB_BASE="https://gitlab.com/xonotic"
RELEASE_URL="https://dl.xonotic.org/xonotic-0.8.6.zip"

# Repos to clone: local_dir  remote_repo.git
DATA_REPOS=(
    "xonotic-data.pk3dir   xonotic-data.pk3dir.git"
    "xonotic-music.pk3dir  xonotic-music.pk3dir.git"
    "xonotic-maps.pk3dir   xonotic-maps.pk3dir.git"
)

# Dirs to exclude from xonotic-data.pk3dir (dev-only, not runtime assets)
DATA_EXCLUDE_DIRS=(qcsrc .tx cmake data demos .tmp)

# Font dirs to extract from the main xonotic.git repo via sparse checkout
FONT_DIRS=(
    font-dejavu.pk3dir
    font-nimbussansl.pk3dir
    font-unifont.pk3dir
    font-xolonium.pk3dir
)

# Map pk3 filenames inside the release zip (under Xonotic/data/)
MAP_PK3S=(
    xonotic-20230620-maps.pk3
    xonotic-20230620-nexcompat.pk3
)

skip_music=false
skip_maps=false
expect_dest=false
for arg in "$@"; do
    if $expect_dest; then
        ASSETS_DIR="$(mkdir -p "$arg" && cd "$arg" && pwd)"
        expect_dest=false
        continue
    fi
    case "$arg" in
        --no-music) skip_music=true ;;
        --no-maps)  skip_maps=true ;;
        --dest)     expect_dest=true ;;
        --help|-h)
            echo "Usage: $0 [--no-music] [--no-maps] [--dest <dir>]"
            exit 0
            ;;
        *)
            echo "Unknown option: $arg"
            exit 1
            ;;
    esac
done
if $expect_dest; then
    echo "--dest requires a directory argument"
    exit 1
fi

info()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33mWARN:\033[0m %s\n' "$*"; }
error() { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; }

# ── Retry a command with exponential backoff (for flaky network ops) ──────
retry() {
    local max="$1"; shift
    local attempt=1 delay=5
    while true; do
        if "$@"; then
            return 0
        fi
        if [ "$attempt" -ge "$max" ]; then
            return 1
        fi
        warn "  attempt $attempt/$max failed; retrying in ${delay}s..."
        sleep "$delay"
        attempt=$((attempt + 1))
        delay=$((delay * 2))
    done
}

# ── git clone with retry + partial-clone cleanup ──────────────────────────
# A clone that dies mid-transfer ("RPC failed; curl 56", "early EOF",
# "fetch-pack: invalid index-pack output") leaves a half-written dir that
# would block a re-clone, so we wipe it before each retry. http.postBuffer is
# bumped because it's the canonical mitigation for the curl-56 RPC failure.
# Usage: git_clone_retry <url> <dest> [extra git-clone flags...]
git_clone_retry() {
    local url="$1" dest="$2"; shift 2
    local max=4 attempt=1 delay=5
    while true; do
        if git -c http.postBuffer=524288000 clone "$@" "$url" "$dest"; then
            return 0
        fi
        rm -rf "$dest"
        if [ "$attempt" -ge "$max" ]; then
            error "clone of $url failed after $max attempts (network?)"
            return 1
        fi
        warn "clone attempt $attempt/$max failed; cleaning up and retrying in ${delay}s..."
        sleep "$delay"
        attempt=$((attempt + 1))
        delay=$((delay * 2))
    done
}

require_cmd() {
    if ! command -v "$1" &>/dev/null; then
        error "Required command '$1' not found. Please install it."
        exit 1
    fi
}

require_cmd git

mkdir -p "$ASSETS_DIR"

# Ensure Godot's editor skips this tree — the game reads it via its own VFS (raw byte
# reads), not Godot's resource importer. Without this the editor tries to import every
# pk3dir .tga/.ogg/.ttf and spams errors (e.g. Xonotic's tiny 1x1 color-swatch TGAs).
# (Written beside ASSETS_DIR so a --dest download into another Godot project is covered too.)
if [ ! -f "$(dirname "$ASSETS_DIR")/.gdignore" ]; then
    printf '# Consumed by the game VFS, not Godot'\''s importer — skip this tree.\n' \
        > "$(dirname "$ASSETS_DIR")/.gdignore"
fi

# ── Clone a pk3dir git repo (shallow, single-branch) ──────────────────────
clone_repo() {
    local dir="$1" repo="$2"
    local dest="$ASSETS_DIR/$dir"

    if [ -d "$dest/.git" ] || [ -f "$dest/.git" ]; then
        info "$dir: already cloned, pulling latest..."
        # ff-only first (quiet); fall back to a retried merge pull, which also
        # covers a transient drop during the ff-only attempt.
        git -C "$dest" pull --ff-only 2>/dev/null || retry 3 git -C "$dest" pull
        return
    fi

    if [ -d "$dest" ] && [ "$(ls -A "$dest" 2>/dev/null)" ]; then
        info "$dir: directory exists (non-git), skipping"
        return
    fi

    info "$dir: cloning from $GITLAB_BASE/$repo ..."
    git_clone_retry "$GITLAB_BASE/$repo" "$dest" --depth 1 --single-branch
}

# ── Remove dev-only directories from xonotic-data.pk3dir ──────────────────
prune_data_dev_dirs() {
    local dest="$ASSETS_DIR/xonotic-data.pk3dir"
    for d in "${DATA_EXCLUDE_DIRS[@]}"; do
        if [ -d "$dest/$d" ]; then
            info "  removing dev-only dir: $d/"
            rm -rf "$dest/$d"
        fi
    done
}

# ── Clone font pk3dirs from the main xonotic.git (sparse checkout) ────────
clone_fonts() {
    local any_missing=false
    for fd in "${FONT_DIRS[@]}"; do
        if [ ! -d "$ASSETS_DIR/$fd" ] || [ -z "$(ls -A "$ASSETS_DIR/$fd" 2>/dev/null)" ]; then
            any_missing=true
            break
        fi
    done

    if ! $any_missing; then
        info "Font dirs: all present, skipping"
        return
    fi

    info "Font dirs: fetching from xonotic.git (sparse checkout)..."
    local tmp_dir
    tmp_dir="$(mktemp -d)"
    trap "rm -rf '$tmp_dir'" RETURN

    if ! git_clone_retry "$GITLAB_BASE/xonotic.git" "$tmp_dir/xonotic" \
        --depth 1 --single-branch --filter=blob:none --sparse; then
        rm -rf "$tmp_dir"; trap - RETURN
        error "font repo clone failed after retries"
        return 1
    fi

    pushd "$tmp_dir/xonotic" >/dev/null
    local sparse_paths=()
    for fd in "${FONT_DIRS[@]}"; do
        sparse_paths+=("data/$fd")
    done
    git sparse-checkout set "${sparse_paths[@]}"
    popd >/dev/null

    for fd in "${FONT_DIRS[@]}"; do
        if [ -d "$tmp_dir/xonotic/data/$fd" ]; then
            if [ ! -d "$ASSETS_DIR/$fd" ] || [ -z "$(ls -A "$ASSETS_DIR/$fd" 2>/dev/null)" ]; then
                info "  copying $fd"
                cp -r "$tmp_dir/xonotic/data/$fd" "$ASSETS_DIR/$fd"
            fi
        else
            warn "  $fd not found in xonotic.git sparse checkout"
        fi
    done

    rm -rf "$tmp_dir"
    trap - RETURN
}

# ── Download compiled map pk3s from the official release ──────────────────
download_maps() {
    local any_missing=false
    for pk3 in "${MAP_PK3S[@]}"; do
        if [ ! -f "$ASSETS_DIR/$pk3" ]; then
            any_missing=true
            break
        fi
    done

    if ! $any_missing; then
        info "Map pk3s: all present, skipping"
        return
    fi

    require_cmd curl
    require_cmd unzip

    info "Map pk3s: downloading from official release..."
    local tmp_dir
    tmp_dir="$(mktemp -d)"
    trap "rm -rf '$tmp_dir'" RETURN

    local zip_path="$tmp_dir/xonotic-release.zip"

    local attempt=1 max=4 delay=5
    while true; do
        info "  downloading $RELEASE_URL (~960 MB)..."
        # -C - resumes a partial download across attempts; --fail turns HTTP
        # errors / connection resets into a non-zero exit so the loop retries.
        if curl -L --fail --progress-bar -C - -o "$zip_path" "$RELEASE_URL"; then
            # Full transfer — sanity-check the archive isn't truncated (a
            # readable central directory means the tail arrived intact).
            if unzip -l "$zip_path" >/dev/null 2>&1; then
                break
            fi
            warn "  downloaded zip is corrupt/truncated; discarding and re-downloading"
            rm -f "$zip_path"
        fi
        if [ "$attempt" -ge "$max" ]; then
            rm -rf "$tmp_dir"; trap - RETURN
            error "  release zip download failed after $max attempts"
            return 1
        fi
        warn "  download attempt $attempt/$max failed; retrying in ${delay}s..."
        sleep "$delay"
        attempt=$((attempt + 1))
        delay=$((delay * 2))
    done

    for pk3 in "${MAP_PK3S[@]}"; do
        if [ ! -f "$ASSETS_DIR/$pk3" ]; then
            info "  extracting $pk3..."
            unzip -j -o "$zip_path" "Xonotic/data/$pk3" -d "$ASSETS_DIR" 2>/dev/null || {
                warn "  $pk3 not found in release zip (may have different name)"
            }
        fi
    done

    rm -rf "$tmp_dir"
    trap - RETURN
}

# ── Main ──────────────────────────────────────────────────────────────────

info "Downloading Xonotic assets into $ASSETS_DIR"
echo

# 1. Clone data repo
clone_repo "xonotic-data.pk3dir" "xonotic-data.pk3dir.git"
prune_data_dev_dirs

# 2. Clone music repo (optional)
if $skip_music; then
    info "Skipping music (--no-music)"
else
    clone_repo "xonotic-music.pk3dir" "xonotic-music.pk3dir.git"
fi

# 3. Clone maps source repo
clone_repo "xonotic-maps.pk3dir" "xonotic-maps.pk3dir.git"

# 4. Fonts from the main xonotic.git (sparse checkout)
clone_fonts

# 5. Compiled map pk3s from the official release (optional)
if $skip_maps; then
    info "Skipping compiled map pk3s (--no-maps)"
else
    download_maps
fi

echo
info "Done! Assets are in: $ASSETS_DIR"
info "The game VFS will mount this directory automatically on launch."
