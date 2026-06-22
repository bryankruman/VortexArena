#!/usr/bin/env python3
"""Generate planning/parity/PORTING-PLAN.md from the wave dependency analysis.

Usage:
    python tools/parity-plan-gen.py [path-to-analysis-output.json]

If a path is given (the raw `parity-plan-deps` workflow output), its `result` block is
persisted to planning/parity/_plan-analysis.json and used. Otherwise the persisted copy
is read. The plan is data-grounded: Wave 2/3 tables and seam fan-out come from the analysis;
the wave model, Wave-1 seam catalog, hot-file set, and exit criteria are the hand-authored
scaffold.

Conflict-free parallelism model: the unit of parallel work is a PORT FILE (one agent owns one
file). Wave 1 owns the cross-cutting HOT files (everything else depends on them); Wave 2 is the
long tail of per-unit gameplay files; Wave 3 is the client/presentation files.
"""
from __future__ import annotations
import json, sys, pathlib
from collections import defaultdict

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "planning" / "parity"
STORE = OUT / "_plan-analysis.json"

GAMEPLAY_CATS = {"gametype","weapon","mutator","item","scoring","damage","monster","turret","vehicle","physics","mapobject","server","bot","net"}
PRESENT_CATS = {"client","effect","sound","notification"}
IMP_RANK = {"H":3,"M":2,"L":1}

# Hot, cross-cutting files owned by Wave 1 (everything else depends on them). Matched by basename.
WAVE1_FILES = {
 "GameWorld.cs","RoundHandler.cs","DamageSystem.cs","WeaponFiring.cs","WeaponFireDriver.cs",
 "SpawnSystem.cs","MutatorHooks.cs","GametypeStatusBlock.cs","NetGame.cs","ModIconsPanel.cs",
 "NetEntity.cs","Projectiles.cs","ServerNet.cs",
}

# Wave 1 shared-seam catalog (hand-authored; fan-out filled from data).
WAVE1 = [
 ("W1-round-handler",  "Round-handler tick + per-gametype callback dispatch",
  "Wire the dead Common RoundHandler so it actually ticks and invokes each round-based gametype's "
  "CanRoundStart/CanRoundEnd/CheckWinner, round-start+end timing, grace/warmup countdown, and round "
  "timelimit/stalemate. Owns the round + gametype drive in GameWorld.cs.",
  ["S2"], "GameWorld.cs, RoundHandler.cs", "critical"),
 ("W1-projectile-net", "Networked projectile entity (model + trail + shoot-down)",
  "Assign NetEntityKind.Projectile and network a real projectile entity with model/skin + smoke trail, and "
  "route RadiusDamage onto projectiles so they can be shot down. Unblocks every projectile weapon's visuals.",
  ["S1","projectile-shootdown"], "NetEntity.cs, Projectiles.cs", "critical"),
 ("W1-weaponfire-fx",  "Shared weapon-fire FX/audio hooks (tracer, ricochet, casings, woosh)",
  "Add the shared FireBullet/fire-path emission seam: bullet tracer trailparticles, impact ricochet PlayRic "
  "dispatch, shell-casing eject, melee woosh. Unblocks every hitscan weapon's signature FX/audio at once.",
  ["S3","S8"], "WeaponFiring.cs, EffectSystem, SoundSystem", "critical"),
 ("W1-input-impulse",  "Player input / impulse routing",
  "Route currently-unbound impulses to handlers: flag throw/pass/request-pass, key voluntary +use drop, "
  "objective +use, weapon reload routing for non-Reloadable weapons (tuba), cheat impulses.",
  ["S5","cheat-impulse"], "WeaponImpulses.cs / impulse routing", "critical"),
 ("W1-damage-channel", "Damage deathtype/hittype channel + per-entity damage scale",
  "Carry the full string deathtype/hittype through the int-keyed damage path (monster/turret/vehicle/special "
  "deaths) and let gametypes/entities consult per-entity DamageScale/DamageForceScale.",
  ["int-deathtype","DamageScale","event-damage-routing"], "DamageSystem.cs", "high"),
 ("W1-mod-icons",      "Gametype mod-icon HUD feed (server status -> client render)",
  "Establish the gametype status-block feed end-to-end: server status send, NetGame Kind dispatch, ModIconsPanel "
  "mode framework. Wave-2 gametypes push state; Wave-3 adds per-mode render.",
  ["S9"], "GametypeStatusBlock.cs, NetGame.cs, ModIconsPanel.cs", "high"),
 ("W1-startitems",     "Gametype start-loadout / weapon-arena wiring",
  "Wire SetStartItems gametype subscription + the dead SetWeaponArena .Call site so modes can set spawn loadouts "
  "and arenas (CA 200/200 'most', etc.). Owns the start-items path in SpawnSystem.cs.",
  ["setstartitems","weapon-arena","start_weapons"], "SpawnSystem.cs", "high"),
 ("W1-notify-driver",  "Notification driver edges (frags-left, leader-visible, lead-change)",
  "Add the shared per-frame notification computations in the match/frame driver: 'N frags left' fragsleft, LMS "
  "leader-visibility edges, lead-change. (Per-event Send calls stay with each Wave-2 unit.)",
  ["S4"], "match/frame driver, notification helpers", "high"),
 ("W1-mutator-hooks",  "Mutator hook-dispatch wiring (revive dead .Call chains)",
  "Many mutator hooks are subscribed but their .Call dispatch site is missing, so the mutator does nothing live. "
  "Wire the dispatch sites so subscribed hooks fire on the live path.",
  ["dead-mutator-hook"], "MutatorHooks.cs + dispatch sites", "high"),
 ("W1-ball-frame",     "Ball entity framework (spawn/think/touch/carry)",
  "Shared ball lifecycle: the host-side procedural ball spawner (SpawnBall has zero callers today, so no ball "
  "ever exists), think/idle-reset glide, touch/carry, respawn. Unblocks Keepaway, Team Keepaway, Nexball.",
  ["S12","ball lifecycle"], "shared ball entity + GameWorld spawn", "high"),
 ("W1-objective-csqc", "Objective entity networking + CSQC (generators / control points / objectives)",
  "Networked objective world models + icons + progressive damage-model swaps + death-cam for Onslaught "
  "generators/control-points and Assault objectives; plus onslaught_link graph-edge resolution.",
  ["S11","onslaught_link","radarlink"], "Onslaught/Assault objective net+render", "medium"),
 ("W1-alpha-net",      "Entity alpha networking (default_player_alpha + per-entity Alpha)",
  "Seed default_player_alpha at worldspawn, add a per-entity Alpha network field, render transparency. Unblocks "
  "the Cloaked mutator and any fade/invisibility.",
  ["S7"], "NetEntity.Alpha, world init, PlayerModel render", "medium"),
 ("W1-csqcmodel",      "Player-model animation-decide + effect/color pipeline",
  "Shared player-model anim-decide + effect-flag/forced-color/glow pipeline that client presentation consumes "
  "(frozen tint, role colors, forced skins).",
  ["S10"], "Csqc model pipeline", "medium"),
 ("W1-entity-frame",   "Turret/Vehicle entity framework (cvars, networking, damage, touch)",
  "Shared turret+vehicle base: cvar table, CSQC model/aim networking, event-damage routing, touch/enter-exit "
  "dispatch every individual turret/vehicle depends on. (Niche modes — lower priority.)",
  ["turret-cvar","turret-networking","turret-vehicle-event","vehicle-touch","vehicle-flag-carrier"],
  "Turrets/ + Vehicles/ shared base", "medium"),
 ("W1-mapvote-net",    "Mapvote / end-match vote networking",
  "Networked end-match map/gametype vote entity + client vote-screen feed.",
  ["mapvote-net"], "MapVoting.cs + net + vote screen", "medium"),
 ("W1-mapobject-seam", "Map-object host seam (centerprint-net, sounds, event-damage, movetype parse)",
  "Shared map-object plumbing several func_/trigger_/target_ entities need: centerprint networking, sounds key, "
  "event-damage, platform movetype parse.",
  ["mapobject-centerprint","mapobject-sounds","mapobject-event-damage","mapobject-host-seam","set-platmovetype"],
  "MapObjects/ shared + MapLoader", "medium"),
 ("W1-bot-aim",        "Per-weapon bot-aim framework",
  "The bot weapon-aim (wraim) framework per-weapon bot tuning hangs off. Framework only — per-weapon values are "
  "Wave-2 weapon work.",
  ["bot-wraim"], "Bot/ aim framework", "low"),
 ("W1-active-mutators","Active-mutators advertisement (BuildMutatorsString + MUT_ HUD icons)",
  "BuildMutatorsString/PrettyString + the MUT_* active-mutator HUD-icon registry. Broad but purely cosmetic — "
  "defer if trimming Wave 1.",
  ["S6"], "mutator string builder + HUD", "low"),
]


def load(argv):
    if len(argv) > 1:
        doc = json.loads(pathlib.Path(argv[1]).read_text(encoding="utf-8", errors="replace"))
        result = doc.get("result", doc)
        STORE.write_text(json.dumps(result, indent=1), encoding="utf-8")
        return result
    return json.loads(STORE.read_text(encoding="utf-8"))


def basename(p):
    return p.replace("\\", "/").rsplit("/", 1)[-1]


def shard_of(unit_label):
    # fine-grained labels look like "<shard>.<area>.<feature>" or "<shard>:..."; recover the shard id
    return unit_label.split(":", 1)[0].split(".", 1)[0]


def fanout(clusters, keys):
    n = set()
    for c in clusters:
        for u in c.get("units", []):
            for s in u.get("seams_needed", []):
                if any(k.lower() in s.lower() for k in keys):
                    n.add(u["unit"])
    return len(n)


def esc(s):
    return (s or "").replace("|", "\\|").replace("\n", " ").strip()


def short_seams(seams):
    out = []
    for s in sorted(seams):
        if s.startswith("S") and len(s) <= 4:
            out.append(s)
        elif s.startswith("new:") or s.startswith("+"):
            out.append("+" + s.split(":", 1)[-1].lstrip("+")[:16])
    seen, uniq = set(), []
    for x in out:
        if x not in seen:
            seen.add(x); uniq.append(x)
    return ", ".join(uniq[:6]) or "-"


def group_by_file(clusters):
    """Group work-items by primary file. Returns {file: rec}. rec has cat, items, impact, seams, shards, heads."""
    g = {}
    for c in clusters:
        for u in c.get("units", []):
            fs = u.get("files") or ["(unspecified)"]
            f = fs[0]
            r = g.setdefault(f, {"file": f, "cat": u.get("category", "?"), "items": 0,
                                 "impact": "L", "seams": set(), "shards": set(), "heads": []})
            r["items"] += 1
            if IMP_RANK.get(u.get("impact", "L"), 1) > IMP_RANK.get(r["impact"], 1):
                r["impact"] = u.get("impact", "L")
            # category: prefer a gameplay category if the file mixes (keeps it out of Wave 3 by accident)
            if u.get("category") in GAMEPLAY_CATS:
                r["cat"] = u["category"]
            for s in u.get("seams_needed", []):
                r["seams"].add(s if s.startswith("S") else "new:" + s.split(":", 1)[1].strip() if ":" in s else s)
            r["shards"].add(shard_of(u["unit"]))
            if u.get("headline"):
                r["heads"].append(u["headline"])
    return g


def merged_head(rec, cap=300):
    h = "; ".join(rec["heads"])
    if len(h) > cap:
        h = h[:cap - 1] + f"… (+{rec['items']} gaps total — see shards)"
    return h


def main():
    data = load(sys.argv)
    clusters = data["clusters"]
    g = group_by_file(clusters)
    total_items = sum(r["items"] for r in g.values())

    absorbed = {f: r for f, r in g.items() if basename(f) in WAVE1_FILES}
    rest = {f: r for f, r in g.items() if basename(f) not in WAVE1_FILES}
    wave2 = {f: r for f, r in rest.items() if r["cat"] in GAMEPLAY_CATS}
    wave3 = {f: r for f, r in rest.items() if r["cat"] in PRESENT_CATS}

    L = []
    P = L.append
    P("# Parity Porting Plan — wave-based, maximally parallel")
    P("")
    P(f"_Generated by `tools/parity-plan-gen.py` from `_plan-analysis.json` "
      f"({total_items} work-items over {len(g)} port files). Regenerate after any `parity-diff --update`._")
    P("")
    P("Execution plan for closing the gaps in [PARITY-GAPS.md](PARITY-GAPS.md), in **4 waves**. The ordering is "
      "forced by one fact: a small set of **shared seams** living in cross-cutting *hot files* gates most gaps. "
      "Once those exist, the rest is overwhelmingly independent and runs in two big parallel pushes, then a "
      "verification gate.")
    P("")
    P("## The parallelism rule (read first)")
    P("")
    P("**One agent owns one file.** Two agents must never edit the same file in the same wave — that is the entire "
      "reason for the wave split. The cross-cutting **hot files** are owned by **Wave 1**; after Wave 1, every "
      "Wave-2 file is touched by exactly one agent, so 100+ agents run with zero edit conflicts. Run a wave's "
      "agents in parallel (use `isolation: worktree` when they touch sibling files), then **barrier**: build + "
      "tests + `parity-diff` before the next wave. Each row below names the **file** an agent owns and the "
      "**shard(s)** (`registry/<shard>.yaml` + `specs/<shard>.md`) that hold its exact gaps/constants.")
    P("")
    P("## Wave map at a glance")
    P("")
    P("| Wave | What | Parallel agents | Depends on |")
    P("|---|---|---|---|")
    P(f"| **1 — Seams** | {len(WAVE1)} shared-infra capabilities in {len(absorbed)} hot files | ~{len(WAVE1)} | — |")
    P(f"| **2 — Gameplay leaves** | every gameplay unit's own-file gaps | ~{len(wave2)} (1 per file) | Wave 1 |")
    P(f"| **3 — Presentation** | client/HUD/FX/audio that renders Wave-2 state | ~{len(wave3)} (1 per file) | Wave 2 |")
    P("| **4 — Verify** | build, in-game checks, `parity-diff --update` re-baseline | sequential gate | 1–3 |")
    P("")
    P(f"122 of {total_items} work-items are fully self-contained leaves (no shared dependency); ~255 need ≥1 seam "
      "— which is why Wave 1, though small, is the only real bottleneck.")
    P("")

    # WAVE 1
    P("## Wave 1 — Shared seams (the only bottleneck)")
    P("")
    P("Independent capabilities, each **owning its hot file(s)** — they run in parallel. `fan-out` = distinct units "
      "unblocked once it lands. Do `critical`/`high` first; `low` rows are cosmetic and can be deferred.")
    P("")
    P("| seam | pri | fan-out | what to build | owns (files) |")
    P("|---|---|---|---|---|")
    for sid, name, desc, keys, owns, prio in sorted(WAVE1, key=lambda r: {"critical":0,"high":1,"medium":2,"low":3}[r[5]]):
        P(f"| **{sid}** — {esc(name)} | {prio} | {fanout(clusters, keys)} | {esc(desc)} | {esc(owns)} |")
    P("")
    if absorbed:
        P("Wave 1 also **absorbs every work-item that lands in a hot file** (one owner per file). Hot files and "
          "their work-item counts:")
        P("")
        P("| hot file | work-items | top shards feeding it |")
        P("|---|---|---|")
        for f, r in sorted(absorbed.items(), key=lambda kv: -kv[1]["items"]):
            P(f"| `{esc(f)}` | {r['items']} | {esc(', '.join(sorted(r['shards'])[:6]))} |")
        P("")
    P("**Exit:** builds; tests green; each seam has a live caller proven by a smoke test or by `parity-diff` "
      "showing the dependent rows are now reachable.")
    P("")

    # WAVE 2
    P("## Wave 2 — Gameplay leaves (the big parallel push)")
    P("")
    P(f"One agent per file ({len(wave2)} files), each editing only that file. Given the Wave-1 seams these are "
      "mutually independent. Grouped by subsystem; within a group do **H** impact first. `seams` = the Wave-1 "
      "items this file consumes; `shards` = which registry shards hold its gaps.")
    P("")
    order = ["gametype","weapon","mutator","item","scoring","damage","server","bot","net","physics","mapobject","monster","turret","vehicle"]
    bycat = defaultdict(list)
    for f, r in wave2.items():
        bycat[r["cat"]].append(r)
    for cat in order:
        rows = sorted(bycat.get(cat, []), key=lambda r: (-IMP_RANK.get(r["impact"], 1), r["file"]))
        if not rows:
            continue
        P(f"### {cat} — {len(rows)} files")
        P("")
        P("| file | imp | seams | shards | what to port (own-file work) |")
        P("|---|---|---|---|---|")
        for r in rows:
            P(f"| `{esc(r['file'])}` | {r['impact']} | {short_seams(r['seams'])} | "
              f"{esc(', '.join(sorted(r['shards'])[:4]))} | {esc(merged_head(r))} |")
        P("")
    P("**Exit:** builds + tests green; `python tools/parity-assemble.py` then "
      "`Workflow{name:\"parity-diff\", args:{scope:\"all\", mode:\"update\"}}` shows the targeted server-side rows "
      "flipped to faithful/live. Remaining `presentation_split` rows are expected (Wave 3).")
    P("")

    # WAVE 3
    P("## Wave 3 — Presentation (renders Wave-2 state)")
    P("")
    P(f"Client/HUD/FX/audio files ({len(wave3)}) that consume the server state produced in Wave 2. Per-gametype "
      "client rendering (e.g. mod-icon cases, overlays) is added by the single agent that owns each shared client "
      "file. Parallelize per file.")
    P("")
    P("| file | imp | seams | shards | client-side work |")
    P("|---|---|---|---|---|")
    for r in sorted(wave3.values(), key=lambda r: (-IMP_RANK.get(r["impact"], 1), r["file"])):
        P(f"| `{esc(r['file'])}` | {r['impact']} | {short_seams(r['seams'])} | "
          f"{esc(', '.join(sorted(r['shards'])[:4]))} | {esc(merged_head(r))} |")
    P("")
    P("**Exit:** the `NEEDS-INGAME-CHECK.md` queue is walked in-game; presentation rows move off `unknown`/"
      "`missing`; `parity-diff` shows no remaining `presentation:missing` in scope.")
    P("")

    # WAVE 4
    P("## Wave 4 — Integration & verification")
    P("")
    P("Sequential gate proving the waves actually closed gaps (not just compiled):")
    P("")
    P("1. **Build + full test suite** green.")
    P("2. **Behavioral pass** — walk [NEEDS-INGAME-CHECK.md](NEEDS-INGAME-CHECK.md): launch the game per scenario "
      "and confirm presentation/liveness rows code-reading couldn't (flag wave/rotation, frozen overlay, mod-icons, "
      "ball physics, tracers/casings, announcer cues).")
    P("3. **Re-baseline** — `python tools/parity-assemble.py`, then "
      "`Workflow{name:\"parity-diff\", args:{scope:\"all\", mode:\"update\"}}` to rewrite the registry to the new "
      "truth and emit `DRIFT-<date>.md` documenting every fixed row.")
    P("4. **No regressions** — a second `parity-diff` (`mode:\"diff\"`) must report **0 regressions**.")
    P("")
    P("## How to execute a wave")
    P("")
    P("Dispatch a wave as a fan-out workflow — one agent per row, pointed at the file it owns and the shard(s) "
      "named in its row (`planning/parity/registry/<shard>.yaml` + `specs/<shard>.md`) for the exact gaps, Base "
      "symbols, and constants. The shard is the work order: implement until its gaps close and its status "
      "dimensions read faithful/live. After the wave, run the exit-criteria checks before the next. Re-run "
      "`python tools/parity-plan-gen.py` after a `parity-diff --update` to regenerate this plan against the "
      "shrunken gap set.")
    P("")
    (OUT / "PORTING-PLAN.md").write_text("\n".join(L), encoding="utf-8")
    print(f"wrote PORTING-PLAN.md | wave1_seams={len(WAVE1)} hot_files={len(absorbed)} "
          f"wave2_files={len(wave2)} wave3_files={len(wave3)} total_items={total_items}")


if __name__ == "__main__":
    main()
