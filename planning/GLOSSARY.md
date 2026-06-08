# Glossary

Shared vocabulary so decisions, specs, and TODOs read the same way to everyone. Quake/idTech terms first, then
XonoticGodot-specific terms.

## Quake / QuakeC / Darkplaces

- **Darkplaces (DP)** — the C engine Xonotic runs on today (an extended Quake engine). Source in `Base/darkplaces/`.
- **QuakeC (QC)** — the C-like scripting language the gameplay is written in. Source in
  `Base/data/xonotic-data.pk3dir/qcsrc/`. Compiled by **gmqcc**.
- **gmqcc** — the QuakeC compiler used (adds `int`/`bool`, a real preprocessor, `[[accumulate]]`, etc.).
- **progs.dat / csprogs.dat / menu.dat** — the three compiled bytecode programs: server, client, menu.
- **SVQC / CSQC / MenuQC** — server-side / client-side / menu QuakeC. `GAMEQC` = CSQC or SVQC.
- **edict** — an entity. Stored as a flat slice of a global field array; `entity.field` is an integer offset.
- **builtin** — an engine function callable from QC by number (`= #14`). ~420 distinct ones across the 3 VMs.
- **field (`.type name`)** — a member added to *every* entity (flat global namespace). E.g. `.float health`.
- **`self` / `other`** — implicit globals the engine sets before calling QC callbacks. Xonotic has mostly
  migrated to an explicit `entity this` parameter.
- **think / touch / use / blocked** — function-pointer fields the engine calls (on a timer, on collision, etc.).
- **nextthink** — schedules the next `.think` call; fires when `0 < nextthink ≤ time`.
- **MOVETYPE_\*** — the engine's per-entity movement integrators (WALK, TOSS, BOUNCE, STEP, PUSH, FLY, NOCLIP…).
- **SV_Physics** — the server simulation loop (one tick): runs StartFrame, player physics, entity movetypes,
  thinks, EndFrame.
- **SV_PlayerPhysics** — player movement. **In Xonotic this lives in QuakeC** (`common/physics/`), not the engine.
- **trace / traceline / tracebox** — sweep a point/AABB through the world; returns a `trace_t`
  (fraction, endpos, plane normal, contents, surfaceflags, texture name, hit entity).
- **brush** — a convex volume defined by planes; the collision representation of map geometry (separate from
  render geometry).
- **area grid** — DP's 2D spatial partition for entity-vs-entity broadphase (`World_LinkEdict`/`EntitiesInBox`).
- **PVS (Potentially Visible Set)** — precomputed BSP visibility; `checkpvs` asks "can A see B".
- **BSP / IBSP v46** — the compiled map format (Quake3-derived; magic `IBSP`, version 46). 17 lumps:
  geometry, lightmaps, brushes, entities, PVS, etc.
- **.map** — the *source* brush format (text) that compiles to BSP. Xonotic ships ~150 of these.
- **Q3 .shader** — a material script: named material = ordered render **stages** + directives (tcMod, deform,
  blendFunc) + **surfaceparm** gameplay flags (lava, clip, sky, slick…). ~121 files.
- **MD3 / IQM / DPM** — model formats. MD3 = vertex-morph (Quake3); IQM/DPM = skeletal. Xonotic uses all three.
- **tag** — a named coordinate frame on a model (MD3 tag or IQM/DPM bone) used as an **attachment socket**
  for weapons/effects. Queried by `gettaginfo`.
- **.framegroups / .skin / .sounds** — model sidecar files: animation ranges, material/team-variant remap,
  voice-line tables.
- **stat** — one of a fixed array of values the engine syncs server→owning-client (health, ammo, movevars).
- **CSQC entity / SendEntity** — a networked entity whose wire representation the QC fully controls
  (`.SendEntity` server callback → `CSQC_Ent_Update` client callback).
- **temp entity (TE)** — a fire-and-forget network event (explosion, gunshot, blood).
- **pk3** — a zip archive of game assets, mounted by the engine's virtual filesystem.
- **effectinfo.txt** — the named-particle-effect database the particle builtins read.
- **d0_blind_id** — DP's zero-knowledge player-identity/crypto system.
- **warpzone** — Xonotic's seamless portal system; depends on BSP surface queries (`getsurface*`).

## XonoticGodot-specific

- **Engine-services facade** — the C# reimplementation of the builtins; the integration seam (`XonoticGodot.Engine`).
- **Fidelity contract** — the enumerated set of behaviors reproduced exactly (see ARCHITECTURE §5).
- **Golden trace** — a (start, mins, maxs, end) → `trace_t` tuple captured from Darkplaces, used to regression-test
  the C# collision service.
- **Track** — a parallel workstream (Infra, Assets, Engine, Gameplay, Net, UI). See
  [`process/tracks-and-ownership.md`](process/tracks-and-ownership.md).
- **Vertical slice** — a thin end-to-end milestone proving a phase (e.g. "walk around a real map").
- **ADR** — Architecture Decision Record (`decisions/`).
- **Mechanical-assist transpiler** — a Roslyn tool that auto-ports the regular QC→C# cases and flags the rest.
  Not a runtime artifact.
