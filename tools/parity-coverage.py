#!/usr/bin/env python3
"""Base->registry coverage ledger for the parity system.

Answers the question the per-unit audits can't: **how much of Base does the registry even
look at?** Every drift pass has found "newly-unmapped Base features" because unit feature
enumeration was top-down; this tool makes the blind spot measurable and drives it to zero.

It enumerates every .qc/.qh under the Base qcsrc tree, extracts every `base_refs` file path
cited anywhere in planning/parity/registry/*.yaml, applies the scope declarations in
planning/parity/coverage-scope.yaml, and buckets every Base file as:

  cited     — at least one registry row cites it (the unit audit is the coverage claim)
  excluded  — deliberately out of parity scope, with a written rationale (coverage-scope.yaml)
  deferred  — known-uncovered, an audit is scheduled/pending (coverage-scope.yaml)
  UNMAPPED  — nobody has ever looked. The actionable number. Target: 0.

Also lints STALE citations (a base_refs path that matches no Base file — typo or moved file).

Outputs:
  planning/parity/COVERAGE.md    human report (summary, per-dir table, largest unmapped files)
  planning/parity/_coverage.json machine artifact (unmapped/stale lists for audit-wave tooling)

Run after any registry change (parity-diff --update, new units) alongside parity-assemble.py:
    python tools/parity-coverage.py
    python tools/parity-coverage.py --base <path-to-qcsrc>   # override the Base tree location
"""
from __future__ import annotations
import argparse, datetime, fnmatch, json, pathlib, re, sys
try:
    import yaml
except ImportError:
    sys.exit("PyYAML required: pip install pyyaml")

ROOT = pathlib.Path(__file__).resolve().parent.parent
REG = ROOT / "planning" / "parity" / "registry"
OUT = ROOT / "planning" / "parity"
SCOPE_FILE = OUT / "coverage-scope.yaml"
DEFAULT_BASE = ROOT.parent / "Base" / "data" / "xonotic-data.pk3dir" / "qcsrc"


SRC_EXTS = (".qc", ".qh", ".inc")  # .inc: the all.inc registries (deathtypes/effects/models/...) are real source
PATH_RE = re.compile(r"[\w][\w./-]*\.(?:qc|qh|inc)\b")


def enumerate_base(base: pathlib.Path) -> dict[str, int]:
    """relative-posix-path -> line count, for every QC source file under qcsrc."""
    files = {}
    for p in base.rglob("*"):
        if p.suffix.lower() in SRC_EXTS and p.is_file():
            rel = p.relative_to(base).as_posix()
            try:
                files[rel] = sum(1 for _ in p.open("r", encoding="utf-8", errors="replace"))
            except OSError:
                files[rel] = 0
    return files


def extract_paths(ref: str) -> list[str]:
    """Pull every QC source path out of a base_refs string. Auditors write refs in several
    shapes — 'path:symbol', 'path (annotation)', 'pathA + pathB', 'path / other' — so extract
    all path-shaped tokens rather than assuming one clean 'path:symbol'."""
    if not isinstance(ref, str):
        return []
    out = []
    for m in PATH_RE.finditer(ref.replace("\\", "/")):
        path = re.sub(r"^\.?/?(qcsrc/)?", "", m.group(0))
        if "/" in path:
            out.append(path)
    return out


def collect_citations() -> tuple[dict[str, set[str]], list[str]]:
    """normalized-path -> set(units citing it); plus per-shard parse warnings."""
    cited: dict[str, set[str]] = {}
    warnings = []
    for shard in sorted(REG.glob("*.yaml")):
        try:
            doc = yaml.safe_load(shard.read_text(encoding="utf-8"))
        except yaml.YAMLError as e:
            warnings.append(f"{shard.name}: YAML parse error: {e}")
            continue
        unit = (doc or {}).get("unit", shard.stem)
        for f in (doc or {}).get("features") or []:
            for ref in f.get("base_refs") or []:
                for path in extract_paths(ref):
                    cited.setdefault(path, set()).add(unit)
    return cited, warnings


def load_scope() -> dict:
    if not SCOPE_FILE.exists():
        return {"exclude": [], "defer": []}
    doc = yaml.safe_load(SCOPE_FILE.read_text(encoding="utf-8")) or {}
    return {"exclude": doc.get("exclude") or [], "defer": doc.get("defer") or []}


def match_scope(path: str, entries: list[dict]) -> dict | None:
    """First scope entry whose pattern matches path. `dir/**` matches the subtree."""
    for e in entries:
        pat = (e.get("pattern") or "").replace("\\", "/")
        if not pat:
            continue
        if pat.endswith("/**"):
            if path.startswith(pat[:-2]):  # keep the trailing '/'
                return e
        elif fnmatch.fnmatch(path, pat) or path == pat:
            return e
    return None


def main():
    ap = argparse.ArgumentParser(description="Base->registry coverage ledger")
    ap.add_argument("--base", type=pathlib.Path, default=DEFAULT_BASE,
                    help=f"Base qcsrc tree (default {DEFAULT_BASE})")
    ap.add_argument("--top", type=int, default=50, help="how many largest unmapped files to list")
    args = ap.parse_args()
    if not args.base.is_dir():
        sys.exit(f"Base qcsrc tree not found: {args.base} (pass --base)")

    base_files = enumerate_base(args.base)
    cited, warnings = collect_citations()
    scope = load_scope()

    # case-insensitive resolution: Base is the canonical case
    canon = {p.lower(): p for p in base_files}
    buckets: dict[str, list[str]] = {"cited": [], "excluded": [], "deferred": [], "unmapped": []}
    defer_satisfied, cited_paths = [], set()
    for ref_path in cited:
        hit = canon.get(ref_path.lower())
        if hit:
            cited_paths.add(hit)
    stale = sorted(p for p in cited if p.lower() not in canon)

    for path in sorted(base_files):
        if path in cited_paths:
            buckets["cited"].append(path)
            if match_scope(path, scope["defer"]):
                defer_satisfied.append(path)
        elif (e := match_scope(path, scope["exclude"])) is not None:
            buckets["excluded"].append(path)
        elif (e := match_scope(path, scope["defer"])) is not None:
            buckets["deferred"].append(path)
        else:
            buckets["unmapped"].append(path)

    lines_of = lambda paths: sum(base_files[p] for p in paths)
    total_files, total_lines = len(base_files), sum(base_files.values())

    # per top-level dir rollup
    def topdir(p): return p.split("/", 1)[0] if "/" in p else "(root)"
    dirs = sorted({topdir(p) for p in base_files})
    per_dir = {}
    for d in dirs:
        row = {}
        for b, paths in buckets.items():
            sub = [p for p in paths if topdir(p) == d]
            row[b] = (len(sub), lines_of(sub))
        dfiles = [p for p in base_files if topdir(p) == d]
        row["total"] = (len(dfiles), lines_of(dfiles))
        per_dir[d] = row

    unmapped_sorted = sorted(buckets["unmapped"], key=lambda p: -base_files[p])
    today = datetime.date.today().isoformat()

    md = [
        "# Base coverage ledger",
        "",
        f"_Generated {today} by `tools/parity-coverage.py` from {len(list(REG.glob('*.yaml')))} registry shards"
        f" against `{args.base}`._",
        "",
        "A Base file is **cited** when any registry row's `base_refs` names it, **excluded** when",
        "[coverage-scope.yaml](coverage-scope.yaml) declares it out of parity scope (with rationale),",
        "**deferred** when an audit is scheduled but not yet landed, and **UNMAPPED** when nobody has",
        "ever looked — the actionable number. Citation is a *claim* that the owning unit audited the",
        "file; it does not by itself prove row-level completeness (the adversarial verify passes do that).",
        "",
        "## Summary",
        "",
        "| bucket | files | lines | % of lines |",
        "|---|---:|---:|---:|",
    ]
    for b in ("cited", "excluded", "deferred", "unmapped"):
        n, ln = len(buckets[b]), lines_of(buckets[b])
        label = b.upper() if b == "unmapped" else b
        md.append(f"| {label} | {n} | {ln} | {ln / max(1, total_lines) * 100:.1f}% |")
    md += [f"| **total** | {total_files} | {total_lines} | 100% |", ""]

    md += ["## Per-directory", "",
           "| dir | total files | cited | excluded | deferred | UNMAPPED files | UNMAPPED lines |",
           "|---|---:|---:|---:|---:|---:|---:|"]
    for d in dirs:
        r = per_dir[d]
        md.append(f"| `{d}/` | {r['total'][0]} | {r['cited'][0]} | {r['excluded'][0]} | "
                  f"{r['deferred'][0]} | {r['unmapped'][0]} | {r['unmapped'][1]} |")
    md.append("")

    if unmapped_sorted:
        md += [f"## Largest UNMAPPED files (top {args.top})", "",
               "| file | lines |", "|---|---:|"]
        for p in unmapped_sorted[:args.top]:
            md.append(f"| `{p}` | {base_files[p]} |")
        md.append("")

    if buckets["deferred"]:
        md += ["## Deferred (audit scheduled)", "",
               "| file | lines | note |", "|---|---:|---|"]
        for p in sorted(buckets["deferred"], key=lambda p: -base_files[p]):
            e = match_scope(p, scope["defer"]) or {}
            md.append(f"| `{p}` | {base_files[p]} | {e.get('note', '')} |")
        md.append("")

    if scope["exclude"]:
        md += ["## Exclusions (deliberately out of scope)", "",
               "| pattern | files | lines | rationale |", "|---|---:|---:|---|"]
        for e in scope["exclude"]:
            sub = [p for p in buckets["excluded"] if match_scope(p, [e]) is e]
            md.append(f"| `{e.get('pattern')}` | {len(sub)} | {lines_of(sub)} | {e.get('rationale', '')} |")
        md.append("")

    if defer_satisfied:
        md += ["## Defer entries now satisfied (remove from coverage-scope.yaml)", ""]
        md += [f"- `{p}` is now cited" for p in sorted(defer_satisfied)]
        md.append("")

    if stale:
        md += ["## STALE citations (base_refs path matches no Base file — fix the row)", ""]
        md += [f"- `{p}`" for p in stale]
        md.append("")

    (OUT / "COVERAGE.md").write_text("\n".join(md), encoding="utf-8")
    (OUT / "_coverage.json").write_text(json.dumps({
        "generated": today,
        "summary": {b: {"files": len(v), "lines": lines_of(v)} for b, v in buckets.items()},
        "unmapped": [{"path": p, "lines": base_files[p]} for p in unmapped_sorted],
        "deferred": sorted(buckets["deferred"]),
        "stale_citations": stale,
    }, indent=1), encoding="utf-8")

    print(f"base files={total_files} lines={total_lines} | cited={len(buckets['cited'])} "
          f"excluded={len(buckets['excluded'])} deferred={len(buckets['deferred'])} "
          f"UNMAPPED={len(buckets['unmapped'])} ({lines_of(buckets['unmapped'])} lines, "
          f"{lines_of(buckets['unmapped']) / max(1, total_lines) * 100:.1f}%) | stale={len(stale)}")
    for w in warnings:
        print("  WARN", w)


if __name__ == "__main__":
    main()
