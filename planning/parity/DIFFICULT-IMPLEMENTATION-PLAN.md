# Difficult-Items Implementation Plan

_Authored 2026-06-28. Executes **all ~210 "difficult (feature build)" items** from
[UNPORTABLE-ANALYSIS.md](UNPORTABLE-ANALYSIS.md) — full scope, vehicles + turrets included (confirmed
2026-06-28). Phased by dependency; each phase is a single Workflow that parallelizes internally. Between
phases the main loop commits, pushes, runs `parity-assemble`, and checks in._

## RESULTS (2026-06-28 — all 5 code phases complete, green, pushed)

| Phase | Commit | Tests | Dims closed |
|---|---|---|---|
| 1 Foundations (framework) | `54d2592` | 2888 | 10 |
| 2 Frameworks + consumers (framework) | `cb7102c` | 2900 | 19 |
| 3a World models — logic (surgical) | `def9a43` | 2900 | 31 |
| 3b World models — render (framework) | `d29e0ae` | 2900 | 9 |
| 4 Bots + gametype logic (surgical) | `c7cece5` | 2902 | 23 |
| 5 Presentation/audio polish (surgical) | `b729987` | 2913 | 15 |

**~107 gap-dimensions closed · tests 2854→2913 (+59) · feature-with-gap 462→445** (plus ~17 render features
built-but-pending-verify, now in `needs-ingame` 34→51). Every phase passed its Gate on the first attempt with
no manual fixes. **Lesson:** Phase 3 had to split — the surgical harness cannot build world-MODEL render
(spawning Godot scene nodes); that work routed through the framework archetype as Phase 3b.

### Remaining (correctly auto-deferred — each a future dedicated wave, NOT surgical)
1. **Bot subsystems:** `bot_cmd` scripting VM, steerlib flocking/swarm/traceavoid, in-game waypoint editor +
   runtime auto-waypointing + `.waypoints` save, jetpack point-to-point navigation, `tracetoss` ballistic
   lead-aim. (Some constants blocked: the repo ships **compiled bot progs, not QC source**.)
2. **Brush-entity per-node render arch:** `func_button` frame texture, `func_breakable` colormod + wreck/debris
   model, `func_clientwall` distance-fade, `bgmscript` ADSR — all blocked by the same fact: `func_*` brush
   faces are baked into the **static map mesh** with no per-entity render node. Needs a render-arch change
   (extract brush → per-entity mesh + a `bgmtime` music clock for ADSR).
3. **Phase 6 Verify** (below) — not a code wave.

## The two workflow archetypes

The existing `_wave-port.workflow.js` (plan → apply → gate → verify) only fits **surgical edits** — "add a
faithful method + wire it" against existing code. Greenfield **subsystems** (a networking channel, a render
primitive, the WaypointSprite framework) have no anchor to edit against and span new files, so they get a
second archetype:

| Archetype | Script | Shape | Used for |
|---|---|---|---|
| **Framework build** | `_framework-build.workflow.js` (new) | Design → Build → Integrate → Gate → Verify, all-opus, one owner per file | Phases 1–2 (new subsystems) |
| **Surgical wave** | `_wave-port.workflow.js` (existing) | Plan(tiered) → Apply → Gate → Verify | Phases 3–5 (wire many consumers onto the frameworks) |

**Conflict-freedom (both archetypes):** exactly one opus agent owns a file for a phase. All edits/builds are
bucketed by target file; disjoint files run fully parallel, same-file work routes to one owner. The hub files
(`ServerNet.cs`, `ClientNet.cs`, `NetGame.cs`, `GameWorld.cs`, `ClientWorld.cs`) are the contention points —
that's why each phase is **one** workflow, never several concurrent ones, and why the dependency ordering
below is hard.

## Dependency graph (why this order)

```
Phase 1 Foundations ──┬─ wepent channel ───────┐
                      ├─ objective/turret stream┼──► Phase 2 frameworks ──► Phase 3 world models
                      └─ view primitives ───────┘            │                    (consumes net + anim)
                                                              └──► Phase 2 view + weapon-visual consumers
Phase 4 bots/gametype-logic  (independent of render/net; sequenced after to avoid GameWorld/ClientWorld churn)
Phase 5 polish               (depends on the render subsystems from 1–3)
Phase 6 verify               (live /verify run — manual, not a code workflow)
```

The animation infra (`DpmFrameDriver`, `ModelAnimator.FollowEntityFrame`) already landed in Wave-17, so world
models only need **networking (Phase 1) + asset wiring**, not new anim machinery.

---

## Phase 1 — Foundations (framework build)

Pure infrastructure; minimal consumers. Three design tracks, run inside one workflow so the hub-file build
owners serialize cleanly.

| Track | Subsystem | Primary units | Key files |
|---|---|---|---|
| `wepent` | Per-weapon / per-player entity-state channel (charge, clip, last-pickup, networked viewmodel frame) | `net-entity-state`, `cl-csqcmodel` | wire structs, `ServerNet.cs`, `ClientNet.cs`, `NetGame.cs` |
| `objstream` | Turret + Onslaught objective networked entity stream (`TNSF_*` analog) | `turret-framework`, `onslaught`, `net-entity-state` | entity feed, `ServerNet.cs`, `ClientNet.cs` |
| `viewprim` | Shared **networked poly-line / cylinder renderer** + **chase-camera mode** + crosshair-chase trace primitive | `cl-view`, `cl-crosshair` | `Beams`/render, camera, `FirstPersonView` |

**Deliverable:** three live, tested channels/primitives with public APIs the later phases consume. No
gameplay visuals yet beyond a smoke-test.

## Phase 2 — Frameworks & consumers (framework build)

Three tracks consuming Phase 1.

| Track | What | Units |
|---|---|---|
| `waypointsprite` | WaypointSprite spawn → network → edge/radar draw subsystem + consumers; maximized/clickable radar | `mutator-waypoints`, `ctf`, `mutator-buffs`, `cl-teamradar` |
| `viewconsumers` | Hook rope-line, porto trajectory preview, classic third-person/chase, crosshair chase, warpzone fixview/teleported-view/camera | `weapon-hook`, `weapon-porto`, `cl-view`, `cl-crosshair`, `mutator-bugrigs`, `warpzones` |
| `wepentconsumers` | Weapon visuals now that `wepent` exists: vortex charge ring/glow/beam, arc tint, electro orb netlink, networked viewmodel frames, nexball power meter | `weapon-vortex`, `weapon-arc`, `weapon-electro`, `cl-viewmodel`, `nexball` |

## Phase 3 — World models & presentation breadth (surgical wave)

High parallelism, per-unit. Consumes Phase 1 networking + existing anim infra. ~90 items.

- **Mainstream models:** `freezetag` (ice block + frozen pose), `mutator-buffs` (carrier glow), `mutator-nades`
  (projectile/orb models + boom visuals), `mapobject-teleporters-portals` (portal disc).
- **Monsters:** `monster-framework`, `monster-golem`, `monster-mage`, `monster-zombie`, `monster-spider`,
  `monster-wyvern`.
- **Vehicles:** `vehicle-framework`, `vehicle-bumblebee`, `vehicle-racer`, `vehicle-raptor`,
  `vehicle-spiderbot`.
- **Turrets:** `turret-framework`, `turret-ewheel`, `turret-flac`, `turret-fusionreactor`, `turret-hellion`,
  `turret-hk`, `turret-machinegun`, `turret-mlrs`, `turret-phaser`, `turret-plasma`, `turret-plasma_dual`,
  `turret-tesla`, `turret-walker`.

## Phase 4 — Bots & gametype logic (surgical wave + bot framework track)

Independent of render/net; sequenced here to avoid `GameWorld.cs`/`ClientWorld.cs` churn against Phase 3.

- **Bot AI core:** `bot-ai` (chooseenemy, steer, lead-aim/tracetoss, dodge, goalrating), `bot-waypoints`
  (routetogoal, movetogoal, bunnyhop, autolink, steerlib, auto-waypointing, editor, file save).
- **Per-gametype bot roles:** `cts`, `freezetag`, `keepaway`, `onslaught`.
- **`bot_cmd` scripting VM** (in `bot-ai`).
- **Gametype edge logic (Cluster K feature half):** `cts` (intermediate checkpoints), `race` (start-grid,
  qualifying), `tdm` (`tdm_team`, map-support), `ctf` (abort_speedrun, drop_special_items, followfc), `clanarena`
  (dead-code wiring — also a quick win), `overkill-weapons`, `nexball`, `keepaway`, `tka`.

## Phase 5 — Presentation / audio polish (surgical wave)

The Cluster L long-tail. ~40 items: `mapobject-func` (mover audio + train/button/plat presentation),
`mapobject-misc` (bgmscript ADSR, clientwall fade), `mapobject-triggers`, `mapobject-target`,
`mapobject-movers-platforms`, `fx-effectinfo`, `fx-sounds`, `fx-deathtypes`, `fx-notifications`, and assorted
weapon/gametype `presentation:partial`/`audio:partial` rows.

## Phase 6 — Verify (manual, not a workflow)

Run the app via `/verify` across CTF / ONS / race / monster / vehicle maps to clear the new visuals plus the
34 [NEEDS-INGAME-CHECK.md](NEEDS-INGAME-CHECK.md) items. Convert `unknown` → confirmed; file regressions as
fixes.

## Execution loop (per phase)

1. Launch the phase Workflow (background).
2. On completion: read the result; if not green, intervene/fix.
3. `dotnet build` + `dotnet test` sanity (the workflow's Gate already does this).
4. `git commit` + `git push` on `claude/parity-port-waves`.
5. `python tools/parity-assemble.py` → refresh `PARITY-GAPS.md` + counts.
6. Check in, then launch the next phase.

Per the 2026-06-28 directive: run **end-to-end**, checking in **between phases**.
