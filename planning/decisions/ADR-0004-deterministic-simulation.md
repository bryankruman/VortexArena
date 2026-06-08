# ADR-0004 — Custom deterministic simulation, not Godot physics

**Status:** Accepted

## Context

Xonotic's game feel is defined by Quake-style movement (air-control, bunnyhopping) running on a fixed 72 Hz tick,
with collision via AABB-vs-brush sweeps. Player movement *already lives in QuakeC* (`SV_PlayerPhysics` in
`common/physics/`), not in the engine. Client-side prediction re-runs the same movement and reconciles against
the server, which requires the simulation to be **deterministic** and **re-runnable**.

Godot's physics (Godot Physics / Jolt) is frame-rate-coupled, not designed for deterministic re-simulation, and
its embedded version can change between patch releases.

## Decision

Build a **custom deterministic simulation core** in `XonoticGodot.Engine`:

- A **72 Hz fixed-tick** accumulator loop (matching `sys_ticrate = 1/72`), decoupled from Godot's render frame.
- Reproduce the `SV_Physics` order: `StartFrame → (per client) PlayerPreThink/move/PlayerPostThink →
  entity MOVETYPE integrators → due thinks → EndFrame`.
- Port the `common/physics/` movement math to deterministic C#.
- Use a **custom collision/trace service** (AABB-vs-brush) — see `specs/determinism-and-physics.md` — not Godot
  rigidbodies. Godot's `PhysicsDirectSpaceState3D` may be used only for *cosmetic*/non-gameplay queries.
- Use a **deterministic PRNG** (not `System.Random`) seeded from the server.

## Consequences

- Movement is re-runnable for prediction/reconciliation (the basis of [ADR-0005](ADR-0005-custom-netcode.md)).
- We own the collision implementation and its fidelity (RISK R3) — mitigated by the golden-trace harness.
- We do not depend on Godot physics determinism or version stability.
- Godot's `CharacterBody3D`/`MoveAndSlide` is *not* used for player movement (we implement the Quake `PM_` math
  ourselves over our own collision queries).

## Alternatives considered

- **Use Godot/Jolt rigidbodies + CharacterBody3D:** rejected — non-deterministic, version-dependent, and the
  slide behavior differs from Quake; would change feel and break prediction.
- **Fixed-point software physics for strict lockstep:** deferred — likely over-engineering given Xonotic already
  smooths prediction error; revisit only if low-divergence float proves insufficient
  ([ADR-0010](ADR-0010-determinism-and-numerics.md)).
