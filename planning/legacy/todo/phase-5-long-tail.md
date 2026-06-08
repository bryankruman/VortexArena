# Phase 5 — Long Tail & Polish

**Goal:** the hard-but-deferrable subsystems, full parity, and production hardening. **Burns down R8, R9, R11,
R12.**
**Exit demo:** shippable parity with the Darkplaces version (within the agreed v1 scope).
**Active tracks:** all, burning down the long tail.

---

## Track G — Gameplay (deferred subsystems)

### G.A Bots (havocbot — `server/bot/`, ~10k LOC)  ↳ OPEN Q6
- ☐ Waypoint navigation + nav loading (`.waypoints` sidecars).
- ☐ Havocbot decision-making (roles, goals, combat); per-gametype bot logic.
- ☐ Aiming/prediction; difficulty tuning.

### G.B Monsters, Turrets, Vehicles
- ☐ Monsters (7 types; STEP movetype AI). ☐ Turrets (14). ☐ Vehicles (6 — custom physics + player attach).

### G.C Minigames
- ☐ The 9 in-game minigames (their own net protocol; `GAMEQC`-gated). Lower priority.

## Track E — Engine (hard facade tail)

### E.A Particles / effects parity (RISK R11)
- ☐ Parse `effectinfo.txt`; build the particle system matching DP spawn/trail; the `te_*` family + `pointparticles`/`trailparticles`.

### E.B Warpzones / portals  ↳ depends: BSP-surface-query facade
- ☐ Implement `getsurface*` (expose parsed BSP/model mesh to game code).
- ☐ Port the warpzone system (`lib/warpzone/`) — seamless portals, camera transform.

### E.C Visibility (RISK R9)
- ☐ `checkpvs`: ship/recompute BSP PVS, or the raycast/occlusion approximation; validate bot/gametype usage.

### E.D Skeletal extras
- ☐ Full `skel_*` CPU skeleton manipulation (procedural aim/bone blending) if any ported content needs it.

## Track N — Networking (services)

### N.A Server browser / master server  ↳ OPEN Q9
- ☐ Replace the menu host-cache builtins with a master-server client + sortable/filterable server list (+ Steam
  optional).
- ☐ Player identity / auth (replace d0_blind_id — OPEN Q8).
- ☐ HTTP/URI services (`uri_get`) for stats/MOTD/downloads as needed.

## Track A — Assets (completeness)
- ☐ Sprites (.spr/.sp2/.spr32), remaining MDL/ZYM models, cubemaps, the full effects asset set.
- ☐ Sidecar completeness (`.sounds` voice tables, `.mapinfo` everywhere).

## Track U — Client (polish)
- ☐ Remaining HUD/menu dialogs; demo playback; spectator; settings completeness; config compatibility (OPEN Q5).

## Cross-cutting — Hardening & parity QA

- ☐ **Performance pass** (RISK R5): kill per-frame allocations; cache StringName/NodePath; pool; GC-pause + frame-time
  budgets on target hardware; external-profiler sign-off.
- ☐ **Parity QA**: A/B vs Darkplaces per gametype/weapon/mutator (damage, timings, pickups, HUD values).
- ☐ Draw-call batching / rendering polish; lightmap/material fidelity sweep.
- ☐ Localization catalogs wired (the `_("...")` strings).
- ☐ Cross-architecture determinism validation if cross-play is in scope (RISK R13).
- ☐ Packaging: client installers, dedicated-server distribution, asset-conversion tooling for end users.

---

## DoD
Feature/behavior parity with the Darkplaces version across the agreed scope; performance within budget on target
hardware; risk register R8/R9/R11 retired; shippable.
