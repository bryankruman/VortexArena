# UW-0042 — Fix/implement various Q3 map entities

- **Source:** `data:bones_was_here/q3_mapents@898b323857e3`
- **Kind:** qc-gameplay
- **Base symbols touched:** `func_door`, `func_plat`, `func_bobbing`, `func_pendulum`, `func_button`, `func_train`, `func_rotating`, `trigger_multiple`, `trigger_hurt`, `trigger_relay`, `trigger_delay`, `trigger_always`, `func_timer`, `target_speaker`, `platforms.qc`, `subs.qc`, `defs.qh`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
21 commits implementing/fixing Q3 map entity behavior: func_plat/door CRUSH spawnflag (instant-kill, no auto-reverse), wait=-1 immediate return, model2 displacement key, trigger_multiple/hurt/relay/delay Q3 spawnflags, new func_timer/trigger_timer entities, door trap kill credits (player attribution), damage-per-tick frequency fixes, player sound system simplification (removed voice/player split). Touches movers (door, plat, bobbing, pendulum, button, train, rotating), triggers (multiple, hurt, relay, delay, impulse, jumppads, viewloc), targets (speaker, speed, kill), and core subs/platforms/defs.

## Portability
qc-gameplay. 100% mapobjects in qcsrc/common/mapobjects/** — pure QuakeC game logic, no engine or platform dependencies. Directly portable to src/XonoticGodot.Common/Gameplay/MapObjects/** (existing subsystems already integrated).

## Completeness (upstream)
Merged to master (not draft/WIP). 20 days old (landed 2026-06-19). Well-tested upstream (2+ weeks review); Xonotic base has no automated mapobject test suite, but branch passed community play-testing (no regression reports). All 21 commits are clean, single-purpose, ready to port.

## Quality
High. Clean, focused commits (one entity/subsystem per commit); well-commented Q3 quirks; refactoring consolidates dead code (removes door_generic_plat_blocked duplication); fixes real bugs (damage-frequency regression, trap kill credits). Matches Base's coding style; no hacks or experimental code.

## Roadmap / design alignment
Serves Vortex Arena directly. Q3/Q3TA map support is a stated port goal. Registry rows (mapobject-movers-platforms, mapobject-triggers) confirm q3compat infrastructure is live (CompatRemaps.IsQ3Compat wired GameWorld.Boot). Upstream work extends the existing q3compat layer with missing entities and fixes; no conflicts with intended_divergence.

## Recommendation
Port. High gameplay impact (Q3 CRUSH, wait=-1, trap credits, new timers); clean implementation; narrow scope (mapobjects only); no netcode/determinism risk; integrates with live q3compat architecture. Recommend porting all 21 commits as a group (coherent feature set). Cross-reference parity registry rows mapobject-movers-platforms, mapobject-triggers, mapobject-target when updating (some behavior may advance effective baseline for those units).
