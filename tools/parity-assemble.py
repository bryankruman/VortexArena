#!/usr/bin/env python3
"""Assemble the Base<->port parity registry into human-readable views.

Reads every planning/parity/registry/*.yaml shard, validates it against the
schema in planning/parity/SCHEMA.md, and (re)generates:

  planning/parity/INDEX.md              every feature, one row, per-dimension status
  planning/parity/PARITY-GAPS.md        gaps ranked by gameplay impact (the backlog)
  planning/parity/NEEDS-INGAME-CHECK.md  rows whose observable status code-reading can't confirm

Run after the build/diff workflows have written the shards:
    python tools/parity-assemble.py
"""
from __future__ import annotations
import sys, pathlib, datetime
try:
    import yaml
except ImportError:
    sys.exit("PyYAML required: pip install pyyaml")

ROOT = pathlib.Path(__file__).resolve().parent.parent
REG = ROOT / "planning" / "parity" / "registry"
OUT = ROOT / "planning" / "parity"

DIMS = ["logic", "values", "timing", "presentation", "audio"]
STATUS = {"faithful", "partial", "stub", "missing", "na", "unknown"}
LIVENESS = {"live", "dead", "partial", "na", "unknown"}

# --- gameplay-impact ranking -------------------------------------------------
# dimension weights: logic & liveness dominate, presentation/audio lower.
DIM_WEIGHT = {"logic": 5, "values": 3, "timing": 3, "presentation": 2, "audio": 1, "liveness": 5}
# how bad each status is (per dimension)
PENALTY = {"missing": 1.0, "stub": 0.8, "partial": 0.5, "unknown": 0.4,
           "faithful": 0.0, "na": 0.0, "dead": 1.0, "live": 0.0}
# category weighting: core gameplay first, niche entities last
CAT_MULT = {
    "gametype": 1.5, "weapon": 1.5, "damage": 1.5, "physics": 1.5, "item": 1.4, "scoring": 1.4,
    "server": 1.2, "net": 1.1, "data": 1.1, "mapobject": 0.9, "mutator": 1.0,
    "client": 0.8, "effect": 0.8, "sound": 0.8, "notification": 0.8, "bot": 0.7,
    "monster": 0.6, "turret": 0.6, "vehicle": 0.6, "misc": 0.7, "menu": 0.5,
}
GLYPH = {"faithful": "OK", "partial": "~", "stub": "stub", "missing": "MISS",
         "na": "-", "unknown": "?", "live": "live", "dead": "DEAD"}


def feature_score(cat: str, f: dict) -> float:
    if f.get("intended_divergence"):
        return 0.0
    st = f.get("status", {}) or {}
    s = 0.0
    for d in DIMS:
        s += DIM_WEIGHT[d] * PENALTY.get(st.get(d, "unknown"), 0.4)
    s += DIM_WEIGHT["liveness"] * PENALTY.get(st.get("liveness", "unknown"), 0.4)
    return round(CAT_MULT.get(cat, 1.0) * s, 2)


def load():
    units, warnings = [], []
    for p in sorted(REG.glob("*.yaml")):
        try:
            doc = yaml.safe_load(p.read_text(encoding="utf-8"))
        except yaml.YAMLError as e:
            warnings.append(f"{p.name}: YAML parse error: {e}")
            continue
        if not isinstance(doc, dict) or "features" not in doc:
            warnings.append(f"{p.name}: missing top-level 'features'")
            continue
        cat = doc.get("category", "misc")
        for f in doc.get("features") or []:
            st = f.get("status", {}) or {}
            for d in DIMS:
                if st.get(d) not in STATUS:
                    warnings.append(f"{p.name}:{f.get('id')}: bad {d}='{st.get(d)}'")
            if st.get("liveness") not in LIVENESS:
                warnings.append(f"{p.name}:{f.get('id')}: bad liveness='{st.get('liveness')}'")
            if f.get("intended_divergence") and not f.get("divergence_rationale"):
                warnings.append(f"{p.name}:{f.get('id')}: intended_divergence without rationale")
            f["_unit"], f["_cat"] = doc.get("unit", p.stem), cat
            f["_score"] = feature_score(cat, f)
        units.append(doc)
    return units, warnings


def stamp():
    # date passed implicitly; avoid Date.now-style nondeterminism complaints in CI by reading mtime-free
    return datetime.date.today().isoformat()


def write_index(units):
    total = sum(len(u.get("features") or []) for u in units)
    # per-dimension histogram
    hist = {d: {} for d in DIMS + ["liveness"]}
    for u in units:
        for f in u.get("features") or []:
            st = f.get("status", {}) or {}
            for d in DIMS + ["liveness"]:
                v = st.get(d, "unknown")
                hist[d][v] = hist[d].get(v, 0) + 1
    lines = [f"# Parity Index", "",
             f"_Generated {stamp()} from {len(units)} units, {total} features._", "",
             "Status: `OK`=faithful `~`=partial `stub` `MISS`=missing `-`=n/a `?`=unknown; "
             "liveness `live`/`DEAD`/`~`/`?`.", "",
             "## Dimension rollup", "", "| dim | " + " | ".join(sorted(STATUS | {"dead"})) + " |",
             "|" + "---|" * (len(sorted(STATUS | {"dead"})) + 1)]
    cols = sorted(STATUS | {"dead"})
    for d in DIMS + ["liveness"]:
        lines.append("| " + d + " | " + " | ".join(str(hist[d].get(c, 0)) for c in cols) + " |")
    lines += ["", "## Features by unit", ""]
    for u in sorted(units, key=lambda x: (x.get("category", ""), x.get("unit", ""))):
        feats = u.get("features") or []
        lines += [f"### `{u.get('unit')}` ({u.get('category')}) — {len(feats)} features", "",
                  "| id | name | L | V | T | P | A | live | conf |",
                  "|---|---|---|---|---|---|---|---|---|"]
        for f in feats:
            st = f.get("status", {}) or {}
            g = lambda d: GLYPH.get(st.get(d, "unknown"), "?")
            div = " *(intended)*" if f.get("intended_divergence") else ""
            lines.append(f"| `{f.get('id')}` | {f.get('name','')}{div} | {g('logic')} | {g('values')} | "
                         f"{g('timing')} | {g('presentation')} | {g('audio')} | {g('liveness')} | {f.get('confidence','?')} |")
        lines.append("")
    (OUT / "INDEX.md").write_text("\n".join(lines), encoding="utf-8")


def write_gaps(units):
    rows = []
    for u in units:
        for f in u.get("features") or []:
            if f["_score"] > 0 and (f.get("gaps") or f["_score"] >= 5):
                rows.append(f)
    rows.sort(key=lambda f: f["_score"], reverse=True)
    lines = ["# Parity Gaps — ranked by gameplay impact", "",
             f"_Generated {stamp()}. {len(rows)} features with gaps, highest-impact first._", "",
             "Score weights logic & liveness highest, then values/timing, then presentation/audio, "
             "scaled by category (core gameplay > niche entities). Intended divergences excluded.", "",
             "| # | score | unit | feature | worst | gaps |", "|---|---|---|---|---|---|"]
    for i, f in enumerate(rows, 1):
        st = f.get("status", {}) or {}
        worst = max((d for d in DIMS + ["liveness"]),
                    key=lambda d: DIM_WEIGHT[d] * PENALTY.get(st.get(d, "unknown"), 0.4))
        gap = "; ".join(f.get("gaps") or []) or "(no gap text)"
        gap = gap.replace("|", "\\|")
        lines.append(f"| {i} | {f['_score']} | `{f['_unit']}` | `{f.get('id')}` | {worst}:{st.get(worst)} | {gap} |")
    (OUT / "PARITY-GAPS.md").write_text("\n".join(lines), encoding="utf-8")
    return rows


def write_ingame(units):
    rows = []
    for u in units:
        for f in u.get("features") or []:
            st = f.get("status", {}) or {}
            needs = (st.get("presentation") == "unknown" or st.get("liveness") == "unknown"
                     or st.get("timing") == "unknown"
                     or (f.get("confidence") == "low" and st.get("presentation") not in ("na", "faithful")))
            if needs:
                rows.append(f)
    lines = ["# Needs in-game check", "",
             f"_Generated {stamp()}. {len(rows)} features whose presentation/liveness/timing "
             "code-reading could not confirm. Verify by running the app and observing._", "",
             "| unit | feature | uncertain | what to look for |", "|---|---|---|---|"]
    for f in rows:
        st = f.get("status", {}) or {}
        unc = ",".join(d for d in ("presentation", "liveness", "timing") if st.get(d) == "unknown") or "low-conf"
        note = (f.get("notes") or (f.get("gaps") or [""])[0] or "").replace("|", "\\|")
        lines.append(f"| `{f['_unit']}` | `{f.get('id')}` | {unc} | {note} |")
    (OUT / "NEEDS-INGAME-CHECK.md").write_text("\n".join(lines), encoding="utf-8")
    return rows


def main():
    units, warnings = load()
    if not units:
        sys.exit("No registry shards found in " + str(REG))
    write_index(units)
    gaps = write_gaps(units)
    ingame = write_ingame(units)
    total = sum(len(u.get("features") or []) for u in units)
    print(f"units={len(units)} features={total} gaps={len(gaps)} needs_ingame={len(ingame)} warnings={len(warnings)}")
    for w in warnings[:50]:
        print("  WARN", w)
    if len(warnings) > 50:
        print(f"  ... +{len(warnings)-50} more warnings")


if __name__ == "__main__":
    main()
