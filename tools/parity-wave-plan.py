#!/usr/bin/env python3
"""Scope the REMAINING parity work into maximally-parallel waves.

Reads the current registry (planning/parity/registry/*.yaml), finds every open-gap feature
(any dimension in {missing,partial,stub,dead}, excluding intended_divergence), maps it to the
port FILE(s) it touches (from port_refs) + a difficulty tier + a work STREAM + whether it
depends on a foundational network/render contract. Prints the wave partition and writes
planning/parity/_remaining-plan.json.

Wave model (unit of parallelism = one port file; one agent owns one file):
  Foundation = the few shared cross-cutting contracts everything else needs (CSQCMODEL render/
               anim/tag/LOD/colormod + wepent net fields, randomseed, ClientInit_misc feed).
  Then the long tail splits into mutually-independent streams (different file trees) that all
  depend at most on Foundation, so they parallelize: gameplay-logic, presentation-render,
  bot-AI, server-admin.
"""
from __future__ import annotations
import re, json, pathlib, collections
import yaml

ROOT = pathlib.Path(__file__).resolve().parent.parent
REG = ROOT / "planning" / "parity" / "registry"
BAD = {"missing", "partial", "stub", "dead"}
DIMS = ["logic", "values", "timing", "presentation", "audio", "liveness"]

FILE_RE = re.compile(r"((?:src|game|tools)/[\w./-]+\.cs)")

# Foundational-contract signals in gap/feature text  -  these gaps can't be RENDERED without a
# shared networked contract that must land first.
FOUND_KW = re.compile(r"csqcmodel|anim_|anim state|tag-attach|tag attach|tag networking|"
                      r"networked render|render subsystem|wire-contract|wire contract|"
                      r"randomseed|random seed|shared .*rng|clientinit_misc|fixclientcvars|"
                      r"colormod.*scale|lod swap|csqcprojectile", re.I)

PRESENT_KW = re.compile(r"csqc|\bhud\b|mod-?icon|world model|world-model|\boverlay\b|render|"
                        r"\bdraw\b|sprite|viewmodel|view-model|glow model|particle|"
                        r"\bbeam\b|\bicon\b|centerprint|scoreboard|crosshair|reticle|"
                        r"trail|explosion sequence|model swap|colormod|radar|waypoint sprite|"
                        r"camera|event.?chase|eventchase|3d model|trajectory preview|rope line", re.I)

HARD_KW = re.compile(r"csqc|network|predict|physics|antilag|anticheat|vote|algorithm|"
                     r"interp|state machine|warpzone|render|protocol|drag subsystem|"
                     r"speedrun|redirect", re.I)

SERVER_ADMIN_UNITS = {
    "sv-cheats", "sv-world-rules", "sv-client-lifecycle", "sv-mapvoting", "sv-ipban",
    "sv-commands-votes", "sv-intermission", "sv-antilag", "sv-chat", "sv-clientkill",
    "sv-spawnpoints",
}

# Shared hot files  -  edits here must be serialized / owned by Foundation, not raced by stream agents.
HOT = {
    "GameWorld.cs", "DamageSystem.cs", "Scores.cs", "ServerNet.cs", "ClientNet.cs",
    "NetGame.cs", "GametypeStatusBlock.cs", "ModIconsPanel.cs", "NetEntity.cs",
    "ClientEntityView.cs", "EntityClasses.cs", "ClientWorld.cs", "SpawnSystem.cs",
}


# The 4 cross-cutting network/render CONTRACTS that downstream rendering can't work without.
FOUNDATION_UNITS = {"cl-csqcmodel", "net-entity-state", "sv-world-rules", "sv-client-lifecycle"}


def stream_of(unit, cat, worst):
    if unit in FOUNDATION_UNITS:
        return "foundation"
    if unit.startswith("bot-") or cat == "bot":
        return "bot"
    if unit in SERVER_ADMIN_UNITS:
        return "server-admin"
    if cat in ("client", "effect", "sound", "notification"):
        return "presentation"
    # dominant-dimension routing: render-heavy units (presentation+audio) -> render wave,
    # logic/values/timing/liveness-heavy -> gameplay-logic wave.
    render_score = worst.get("presentation", 0) + worst.get("audio", 0)
    logic_score = (worst.get("logic", 0) + worst.get("values", 0)
                   + worst.get("timing", 0) + worst.get("liveness", 0))
    return "presentation" if render_score > logic_score else "gameplay"


def tier_of(open_count, text):
    if open_count >= 5 or HARD_KW.search(text):
        return "opus"
    if open_count <= 2 and not re.search(r"logic|timing", text):
        return "haiku"
    return "sonnet"


units = []
file_to_units = collections.defaultdict(set)

for p in sorted(REG.glob("*.yaml")):
    doc = yaml.safe_load(p.read_text(encoding="utf-8"))
    if not isinstance(doc, dict):
        continue
    unit = doc.get("unit", p.stem)
    cat = doc.get("category", "unknown")
    open_feats = []
    files = set()
    found_dep = False
    gap_texts = []
    notes = []
    worst_dims = collections.Counter()
    for feat in doc.get("features", []) or []:
        if not isinstance(feat, dict):
            continue
        if feat.get("intended_divergence"):
            continue
        st = feat.get("status", {}) or {}
        bad_dims = [d for d in DIMS if str(st.get(d, "na")) in BAD]
        if not bad_dims:
            continue
        open_feats.append(feat.get("id", feat.get("name", "?")))
        for d in bad_dims:
            worst_dims[d] += 1
        # collect text for classification
        txt_parts = [feat.get("name", "")]
        txt_parts += [str(g) for g in (feat.get("gaps") or [])]
        txt_parts += [str(r) for r in (feat.get("port_refs") or [])]
        # also note which dims are bad (so stream_of can read "logic: missing")
        txt_parts += [f"{d}: {st.get(d)}" for d in bad_dims]
        ftext = " \n ".join(txt_parts)
        gap_texts.append(ftext)
        # worklist note: feature name + dims-in-gap + the gap descriptions (what the impl agent acts on)
        gaplist = "; ".join(str(g) for g in (feat.get("gaps") or [])) or "(see registry row)"
        note = f"[{feat.get('id','?')} | {','.join(bad_dims)}] {feat.get('name','')}  -  {gaplist}"
        notes.append(note[:900])
        if FOUND_KW.search(ftext):
            found_dep = True
        for ref in (feat.get("port_refs") or []):
            for m in FILE_RE.finditer(str(ref)):
                files.add(m.group(1).split("/")[-1])
    if not open_feats:
        continue
    text = " \n ".join(gap_texts)
    stream = stream_of(unit, cat, worst_dims)
    tier = tier_of(len(open_feats), text)
    for f in files:
        file_to_units[f].add(unit)
    units.append({
        "unit": unit, "category": cat, "open_gaps": len(open_feats),
        "stream": stream, "tier": tier, "foundation_dep": found_dep,
        "worst_dims": dict(worst_dims),
        "files": sorted(files),
        "hot_files": sorted(f for f in files if f in HOT),
        "notes": notes,
    })

# ---- partition into WAVES ----
streams = collections.defaultdict(list)
for u in units:
    streams[u["stream"]].append(u)

total_gaps = sum(u["open_gaps"] for u in units)
print(f"=== REMAINING WORK: {len(units)} units with open gaps, {total_gaps} open-gap features ===\n")

# Wave 13 = gameplay-logic + bot + server-admin (server-side correctness, no render dep).
# Wave 14 = foundation contracts. Wave 15 = presentation/render (consumes 13 state + 14 contracts).
WAVES = {
    "13": [u for s in ("gameplay", "bot", "server-admin") for u in streams.get(s, [])],
    "14": streams.get("foundation", []),
    "15": streams.get("presentation", []),
}
WAVE_NAME = {"13": "Gameplay-logic + Bot + Server-admin", "14": "Foundation contracts",
             "15": "Presentation / client render"}

for w in ("13", "14", "15"):
    us = sorted(WAVES[w], key=lambda u: -u["open_gaps"])
    g = sum(u["open_gaps"] for u in us)
    tiers = collections.Counter(u["tier"] for u in us)
    print(f"WAVE {w}  -  {WAVE_NAME[w]:36} : {len(us):3} units, {g:4} gaps | tiers {dict(tiers)}")
    for u in us:
        fd = " [needs-foundation]" if u["foundation_dep"] and w != "14" else ""
        print(f"    {u['tier']:6} {u['unit']:34} {u['open_gaps']:3}g  "
              f"{','.join(f'{k}:{v}' for k,v in sorted(u['worst_dims'].items(), key=lambda x:-x[1]))}{fd}")
    print()
    # emit ready-to-run worklist for the tiered workflow
    wl = [{"unit": u["unit"], "tier": u["tier"], "notes": u["notes"]} for u in us]
    (REG.parent / f"_wave{w}-units.json").write_text(json.dumps(wl, indent=1), encoding="utf-8")

print("=== shared HOT files touched by >1 unit (chunk batches so <=3 touch the same file) ===")
for f, us in sorted(file_to_units.items(), key=lambda kv: -len(kv[1])):
    if f in HOT and len(us) > 1:
        print(f"    {f:24} {len(us):2}: {', '.join(sorted(us))[:104]}")

out = {"units": units, "waves": {w: [u["unit"] for u in WAVES[w]] for w in WAVES},
       "total_units": len(units), "total_gaps": total_gaps}
(REG.parent / "_remaining-plan.json").write_text(json.dumps(out, indent=1), encoding="utf-8")
print("\nwrote _remaining-plan.json + _wave13-units.json + _wave14-units.json + _wave15-units.json")
