# Spec — Determinism, Simulation & Physics Fidelity

Implements [ADR-0004](../decisions/ADR-0004-deterministic-simulation.md) and
[ADR-0010](../decisions/ADR-0010-determinism-and-numerics.md). This is the **fidelity contract** core.
Reference: `Base/darkplaces/sv_phys.c`, `sv_main.c`, `sv_user.c`, `collision.c`, `world.c`, and
`qcsrc/common/physics/`.

## The fidelity contract (reproduce exactly; everything else is free)

1. **72 Hz fixed tick.** `sys_ticrate = 1/72 ≈ 0.0138889 s`. Accumulator-based, sub-stepped, with a frame budget
   (match `SV_Frame`). Decoupled from Godot's render `_process`.
2. **`SV_Physics` order per tick:** `StartFrame` → for each client `{PlayerPreThink; movement; PlayerPostThink}` →
   non-client entity MOVETYPE integrators → due `.think`s → `EndFrame`. Mods schedule on exact tics; ordering is
   observable.
3. **`nextthink` dispatch:** fire when `0 < nextthink ≤ time + frametime`; set `time = max(now, nextthink)`;
   optionally multiple thinks/frame (capped).
4. **`.touch` dispatch:** on a blocking trace, run *both* entities' touch (each sees the other as `other`).
5. **`SV_FlyMove`/`ClipVelocity` slide-and-step:** up to 5 clip planes, `0.7` floor-normal threshold for
   onground, `STOP_EPSILON`/overbounce slide, the three-trace **stair-step** sequence (up `stepheight`, forward,
   down), and the **ticrate-dependent gravity half-step** switch (affects jump heights).
6. **Trace results:** `traceline`/`tracebox` return the full DP `trace_t` — fraction, endpos, plane normal,
   `trace_dphitcontents` (SUPERCONTENTS), `trace_dphitq3surfaceflags`, `trace_dphittexturename`. Game logic reads
   all of these (surfaceflags → slick/ladder/clip; texture name → footsteps/warpzones).
7. **The `common/physics/` movement math** (accel, air-accel, friction, bunnyhop, jump, water) ported as
   deterministic C#.

## Why this is tractable

Player movement **already lives in QuakeC** (`SV_PlayerPhysics`); Darkplaces' built-in movement is *bypassed*.
The engine only owes movement: (a) collision traces, (b) `FL_ONGROUND`/`groundentity`/`waterlevel` bookkeeping,
(c) the 72 Hz tick. Get those three right and feel follows — and (a)/(c) are ours to build, (the movement math is
in the part we port).

## The collision/trace service

Reproduce `traceline`/`tracebox` (DP `collision.c` / `world.c`) — **not** Godot rigidbodies:

- **Brush geometry** from the BSP (Brushes/Brushsides lumps — convex planar half-space sets) is the collision
  world, separate from render geometry, carrying `contentflags`.
- **AABB-vs-brush sweep** via separating-plane enter/leave fraction accumulation
  (`Collision_TraceBrushBrushFloat` semantics); line/point specializations; mesh-triangle traces where needed.
- **Area grid** broadphase (128×128 2D grid; `World_LinkEdict`/`EntitiesInBox`, epoch dedup) for
  entity-vs-entity candidate gathering; `setorigin`/`setmodel` relink.
- **Rotated brush models** via local-space matrix transform (a DP feature beyond vanilla Quake; some maps use it).
- **Filters:** MOVE_NORMAL / NOMONSTERS / MISSILE (±15 box) / WORLDONLY / HITMODEL; `hitsupercontentsmask`.
- Internals run float, results stored double (match DP's mix where it affects fractions).

## Determinism approach (per ADR-0010)

- Fixed timestep, same code path client+server.
- `double` accumulation in movement/collision where the QC did; audit the paths.
- Avoid FMA contraction / fast-math / SIMD reduction reordering in the sim hot path.
- Deterministic PRNG seeded from the server (Xonotic broadcasts a seed).
- Target **low divergence**, not lockstep — rely on the existing prediction-error smoothing as the safety net.

## Golden-trace + parity test harness (own it from Phase 0)

- **Golden traces:** instrument Darkplaces (or capture via demos) to emit `(start, mins, maxs, end, filtermask)
  → trace_t` tuples on real maps; assert the C# collision service matches within tolerance. This is the primary
  guard for RISK R3.
- **Movement parity:** feed identical input logs to the C# movement and to Darkplaces (via a recorded demo or an
  instrumented build); compare position/velocity trajectories; assert divergence stays within the
  error-compensation envelope.
- **Tick/order tests:** unit-test think/touch ordering and `nextthink` edge cases.
- **Determinism test:** same input log on two builds/threads → divergence within envelope (guards R13).

## MOVETYPE integrators to port (non-player entities)

WALK (full walkmove + stepheight), TOSS/BOUNCE/BOUNCEMISSILE/FLYMISSILE/FLY (ballistic + ClipVelocity bounce),
STEP (monsters: gravity + flymove + water/stair), PUSH/FAKEPUSH (doors/plats; push/crush riders), NOCLIP, FOLLOW.
Port from `SV_Physics_Toss/Step/Pusher` + the shared `SV_FlyMove`.
