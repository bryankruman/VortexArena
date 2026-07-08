#!/usr/bin/env python3
"""Generate the launcher release manifest (latest.json) — ADR-0015 §5.

Runs in the release job (.github/workflows/release.yml) after the zips are collected and
checksummed. Reads the release's SHA256SUMS file, maps each zip to its platform key, and emits
the machine-readable manifest the launcher consumes. Attached to every release as `latest.json`;
the stable channel fetches it via the /releases/latest/download/latest.json redirect (no API).

Usage:
  tools/make-manifest.py --tag v0.2.0 --repo bryankruman/XonoticGodot \
      --dir final --sums final/SHA256SUMS-v0.2.0.txt --out final/latest.json \
      [--channel stable] \
      [--assets-name XonoticGodot-assets-<hash12>.zip]        # assets pack in --dir (fresh upload)
      [--assets-url URL --assets-sha256 HEX --assets-size N]  # …or deduped: point at a PREVIOUS
                                                              # release's identical pack (ADR-0015 §4)

Platforms whose zips are absent (e.g. the best-effort macOS job failed) are simply omitted.
"""

import argparse
import json
import os
import re
import sys

# zip-name suffix (tools/package.sh suffix_for) → manifest platform key + the zip's internal
# top-level directory (package.sh zips dist/<target>/, so the export-target name is the root).
SUFFIX_TO_PLATFORM = {
    "windows-x86_64": ("windows-x86_64", "windows-client"),
    "linux-x86_64": ("linux-x86_64", "linux-client"),
    "linux-dedicated-x86_64": ("linux-dedicated-x86_64", "linux-dedicated"),
    "macos-universal": ("macos-universal", "macos-client"),
}

ASSETS_NAME_RE = re.compile(r"^XonoticGodot-assets-([0-9a-f]{12})\.zip$")


def parse_sums(path):
    """SHA256SUMS format: '<hex>  <name>' (sha256sum) or '<hex> *<name>' (binary marker)."""
    sums = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            m = re.match(r"^([0-9a-fA-F]{64})\s+\*?(.+)$", line)
            if m:
                sums[m.group(2)] = m.group(1).lower()
    return sums


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--tag", required=True, help="release tag, e.g. v0.2.0")
    ap.add_argument("--repo", required=True, help="owner/name, e.g. bryankruman/XonoticGodot")
    ap.add_argument("--dir", required=True, help="directory holding the release zips")
    ap.add_argument("--sums", required=True, help="path to the SHA256SUMS file")
    ap.add_argument("--out", required=True, help="output manifest path")
    ap.add_argument("--channel", default="stable")
    ap.add_argument("--assets-name", help="assets pack zip name (in --dir unless --assets-url)")
    ap.add_argument("--assets-url", help="dedupe: URL of an identical pack on a previous release")
    ap.add_argument("--assets-sha256", help="dedupe: that pack's sha256 (GitHub asset digest)")
    ap.add_argument("--assets-size", type=int, help="dedupe: that pack's size in bytes")
    args = ap.parse_args()

    version = args.tag.lstrip("v")
    dl_base = f"https://github.com/{args.repo}/releases/download/{args.tag}"
    sums = parse_sums(args.sums)

    def entry(name, root):
        path = os.path.join(args.dir, name)
        if not os.path.isfile(path):
            return None
        sha = sums.get(name)
        if not sha:
            print(f"WARN: {name} present but missing from {args.sums} — omitted", file=sys.stderr)
            return None
        return {"name": name, "root": root, "size": os.path.getsize(path),
                "sha256": sha, "url": f"{dl_base}/{name}"}

    platforms = {}
    for suffix, (key, root) in SUFFIX_TO_PLATFORM.items():
        fat = entry(f"XonoticGodot-{version}-{suffix}.zip", root)
        core = entry(f"XonoticGodot-{version}-{suffix}-core.zip", root)
        if fat or core:
            platforms[key] = {"fat": fat, "core": core}

    if not platforms:
        print(f"ERROR: no release zips found in {args.dir} — refusing to emit an empty manifest",
              file=sys.stderr)
        return 1

    assets = None
    if args.assets_name:
        m = ASSETS_NAME_RE.match(args.assets_name)
        if not m:
            print(f"ERROR: --assets-name {args.assets_name!r} doesn't match "
                  f"XonoticGodot-assets-<hash12>.zip", file=sys.stderr)
            return 1
        if args.assets_url:  # deduped: identical pack already lives on a previous release
            if not args.assets_sha256 or not args.assets_size:
                print("ERROR: --assets-url needs --assets-sha256 and --assets-size", file=sys.stderr)
                return 1
            assets = {"name": args.assets_name, "version": m.group(1),
                      "size": args.assets_size, "sha256": args.assets_sha256.lower(),
                      "url": args.assets_url}
        else:
            e = entry(args.assets_name, None)
            if e is None:
                print(f"ERROR: assets pack {args.assets_name} not found/checksummed in {args.dir}",
                      file=sys.stderr)
                return 1
            del e["root"]
            assets = {**e, "version": m.group(1)}

    manifest = {
        "schema": 1,
        "version": version,
        "tag": args.tag,
        "channel": args.channel,
        "notesUrl": f"https://github.com/{args.repo}/releases/tag/{args.tag}",
        "assets": assets,
        "platforms": platforms,
    }
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(f"wrote {args.out}: {version} — platforms: {', '.join(sorted(platforms))}"
          f"{' + assets pack' if assets else ''}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
