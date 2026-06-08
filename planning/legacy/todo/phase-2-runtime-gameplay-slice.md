# Phase 2 — Engine Runtime + First Gameplay Slice

**Goal:** real movement feel + the framework + a thin slice of gameplay. **Retires the #3 risk** and most of the
framework port.
**Exit demo:** **local deathmatch on a real map** — Quake-faithful movement, fire a Blaster/Vortex/Machinegun,
take damage, respawn, fight a minimal bot-stub. No networking yet (single-process).
**Active tracks:** E (heavy), G (heavy), I (harness/transpiler), U (basic HUD), A (finish first asset set).
Specs: [`../specs/determinism-and-physics.md`](../../specs/determinism-and-physics.md),
[`../specs/engine-services-facade.md`](../../specs/engine-services-facade.md),
[`../specs/entity-model.md`](../../specs/entity-model.md).

---

## Track E — Engine Runtime (heavy)

### E.1 Simulation core
- ☐ 72 Hz fixed-tick loop with the `SV_Physics` order (StartFrame → player pre/move/post → movetypes → thinks →
  EndFrame).
- ☐ `nextthink` scheduling (`time = max(now, nextthink)`); `.touch` dual-dispatch.
- ☐ MOVETYPE integrators: WALK, TOSS/BOUNCE, STEP, PUSH (doors/plats), NOCLIP, FLY, FOLLOW (port `SV_Physics_*`).

### E.2 Collision/trace service (finish)
- ☐ Full DP `trace_t` result set (contents, surfaceflags, texture name); MOVE_* filters; `hitsupercontentsmask`.
- ☐ Rotated-bmodel local-space clipping; `pointcontents`; `tracetoss`.
- ☐ Golden-trace corpus green across maps. ↳ [I]

### E.3 Facade services (hot path)  ↳ [ADR-0009]
- ☐ Entity mgmt (spawn/remove/find*/setorigin/setmodel with relink), cvars (+autocvars), strings (sprintf/color/UTF-8).
- ☐ Sound (channel manager), 2D draw (HUD primitives + font metrics).
- ☐ Model/tag facade (`gettaginfo`/`setattachment`/`gettagindex`) over A.4 models.

## Track G — Gameplay (heavy)

### G.1 Framework port (`lib/` → `XonoticGodot.Common.Framework`)
- ☐ Entity base class + component layer ([entity-model spec](../../specs/entity-model.md)).
- ☐ Registry attributes + source generators (Weapons/Items/Stats/GameTypes/Mutators) ([ADR-0003]).
- ☐ Mutator/hook event bus (typed events + `ref` args; ordered dispatch).
- ☐ Intrusive-list/container reimplementation; the cl/sv input-source abstraction (`PHYS_*` → `IMovementInputSource`).

### G.2 Movement (the fidelity-critical port)
- ☐ Port `common/physics/` movement math to deterministic C#; run on E.1.
- ☐ Movement-parity tests vs Darkplaces (strafe-jump, bunnyhop, ramps, stairs, water, jumppads). ↳ [I]
- ☐ Tune within the error-compensation envelope ([ADR-0010]).

### G.3 First gameplay slice
- ☐ Player: spawn, health/armor, damage, death, respawn, spawnpoints.
- ☐ 3 weapons end-to-end (Blaster, Vortex, Machinegun) — registry + fire logic + projectiles/hitscan + the
  weapon entity/attachment.
- ☐ Basic items (health/armor/ammo pickups) + the resource system.
- ☐ One gametype: **Deathmatch** (scoring, frag limit).
- ☐ A **bot-stub** (dumb target dummy / minimal nav) — enough to fight; real havocbot is Phase 5 (OPEN Q6).

## Track U — Client (basic HUD)
- ☐ Real predicted-less local movement (single process) reading input → `IMovementInputSource`.
- ☐ Minimal HUD: health/armor/ammo, crosshair, weapon select, frag count (port a few `client/hud` panels).
- ☐ Weapon view-model + muzzle/effect attachment via tags.

## Track A — Assets (finish first set)
- ☐ Convert the maps/models/sounds needed for the DM slice; fix importer long-tail issues surfaced by real use.

## Track I — Infra
- ☐ Movement-parity + determinism test suites in CI.
- ☐ Mechanical-assist transpiler matured enough to accelerate G.3 and the Phase-4 fan-out.

---

## Progress (session 2026-06-04)

Most of the Phase-2 game logic is ported and building (Godot-free libs + Godot host both compile; 14 tests pass).
Done so far:
- ☑ Sim core (72 Hz loop, MOVETYPE integrators, think/touch) + collision/trace service (`XonoticGodot.Engine`).
- ☑ Facade hot path (`EngineServices : IEngineServices`: entities, traces, cvars, sound, clock).
- ☑ **Movement physics** (`Physics/PlayerPhysics`) — deterministic; a determinism+response test is green.
- ☑ **Damage pipeline** (`Gameplay/Damage/DamageSystem`) — armor/health split, knockback, `Combat.Death`; tested.
- ☑ **9 weapons** (blaster, vortex, machinegun, shotgun, devastator, mortar, crylink, electro, hagar) + items/resources + vampire mutator.
- ☑ **Player + SpawnSystem + Deathmatch + MatchController** (spawn→frag→respawn→frag-limit loop).
- ☑ Composition root `GameInit.Boot(IEngineServices)` installs facade + registries + movement + damage.
- ◐ Godot host `game/` (MapLoader/ModelLoader/PlayerController/GameDemo) compiles — **runtime needs a Godot install** (Phase-1 demo).
- ☐ Remaining: DP-captured **golden-trace corpus** (the real movement-parity guard — currently tested only vs determinism + hand expectations); a real bot; HUD; wire source generator; install Godot to actually play the slice.

## DoD
A single-process local deathmatch on a real map that **feels like Xonotic** (movement-parity green), with 3
weapons, items, damage/respawn, and a fightable bot-stub. R3 retired; framework + facade hot-path done.
