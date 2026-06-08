# Phase 0 — Foundations & Spikes

**Goal:** stand up the solution and **prove the three scary things in isolation** before committing to the build.
**Exit demo:** three green spikes + accepted architecture decisions.
**Active tracks:** I (lead), A (spike), E (spike), N (design only), U (minimal).

> Heavily front-loads risk reduction. Nothing here is production code; spikes may be thrown away.

---

## Track I — Infrastructure & Tooling (lead)

- ☐ Pin Godot + .NET versions; create `XonoticGodot.sln` with the project skeleton from
  [`../ARCHITECTURE.md`](../../ARCHITECTURE.md) §2 (`Common`, `Engine`, `Assets`, `Server`, `Client`, `Menu`, `Net`,
  `SourceGen`, `Tests`). ↳ resolves OPEN Q2.
- ☐ Enforce the **no-Godot-reference rule** on `XonoticGodot.Common` (build check).
- ☐ CI: build + unit-test on every commit; nightly for heavier suites.
- ☐ Source-generator hello-world: a trivial `[Register]`→generated-table proof
  ([ADR-0003](../../decisions/ADR-0003-source-generators.md)).
- ☐ Stand up the **golden-trace harness skeleton** (capture format + a runner that diffs C# vs recorded tuples) —
  even if empty, define the contract now ([`../specs/determinism-and-physics.md`](../../specs/determinism-and-physics.md) §"harness").
- ☐ Decide repo/license up front. ↳ resolves OPEN Q1.

## Track A — Assets (Spike A: load a BSP)

- ☐ Minimal **pk3 VFS** read path (open a `.pk3`, list/read a file).
- ☐ **IBSP v46 parser spike:** parse one map's header + Vertices/Faces/Triangles lumps; build a raw `ArrayMesh`
  (no materials/lighting). Confirm magic `IBSP`/version 46.
- ☐ Render it in a throwaway Godot scene; fly the editor camera through it.
- ☐ Write up: lump quirks, patch-tessellation needs, surprises. Reference `Base/darkplaces/model_brush.c`,
  `model_q3bsp.h`.

## Track E — Engine (Spike C: a golden-trace match)

- ☐ Hand-build a tiny brush world (a box + a ramp) in C#.
- ☐ Implement a minimal **AABB-vs-brush `tracebox`** (single brush, no area grid).
- ☐ Capture the same trace from **Darkplaces** (instrumented or via a known case) and assert the C# result matches
  (fraction, plane normal). Proves the collision approach and the harness end-to-end.
- ☐ Implement a bare **72 Hz fixed-tick loop** stepping one entity; confirm tick cadence is renderer-independent.

## Track E/A — Spike B: load a model with a tag

- ☐ **IQM parser spike:** load one player model → `Skeleton3D` + one animation clip from its `.framegroups`.
- ☐ Expose one named **tag/bone** as a world-transform socket; attach a placeholder weapon to it (proves R10
  approach: `gettaginfo`/`setattachment`).

## Track N — Networking (design only)

- ☐ Write the message-format design note (reuse `net.qh` registry design; our own bytes per
  [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)). No code yet.
- ☐ Evaluate MonkeNet/Netfox as a prediction bootstrap; decision recorded.

## Track U — Client (minimal)

- ☐ A throwaway free-fly camera + a placeholder capsule controller to walk the Spike-A map (no real physics yet).

---

## Decision gate (end of Phase 0)

- ☐ Confirm [ADR-0001](../../decisions/ADR-0001-rewrite-strategy.md) scope holds after the spikes.
- ☐ Confirm the entity↔node model ([ADR-0007](../../decisions/ADR-0007-entity-model.md)) and the source-generator
  registry pattern.
- ☐ Update [`../RISK-REGISTER.md`](../../RISK-REGISTER.md): R1/R3 downgraded from "unknown" to "mitigating".

## Progress (session 2026-06-04)

Scaffolding + foundation + first game-logic port landed (built with .NET 9 SDK targeting net8.0; Godot not yet
installed locally, so the Godot host is scaffolded but unbuilt here). **All non-Godot projects build clean from
scratch; 11 integration tests pass.**

- ☑ Solution skeleton: `XonoticGodot.sln` + projects `Common / Engine / Net / Assets / SourceGen / Tests` (+ Godot host
  `XonoticGodot.csproj`), `Directory.Build.props`, `global.json`, `project.godot`, `Main.cs/.tscn`.
- ☑ No-Godot-reference invariant on `Common/Engine/Net/Assets` (verified: no `using Godot`).
- ☑ Foundation in `Common`: `QMath`, `Entity`(+enums), `Registry<T>`+attributes+reflection bootstrap, `HookChain`,
  the engine-services facade interfaces (`IEngineServices`/`ITrace`/`IEntity`/…+`Api`), gameplay base classes.
- ☑ Source-generator (replaces reflection registration at compile time) — built; **wiring into `Common` is the
  next step** (currently the reflection bootstrap is the active path, and it's test-proven).
- ◐ Spike A (BSP): IBSP v46 **parser** done in `XonoticGodot.Formats` (data-level); Godot mesh-build pending an install.
- ◐ Spike B (model+tag): MD3 **parser with tags** done; Godot `Skeleton3D`/attachment pending.
- ◐ Spike C (golden trace): AABB-vs-brush `TraceService` + `ClipVelocity` + `FlyMove` done and unit-tested against
  hand-computed expectations; **DP-captured golden-trace corpus still to build** (the real R3 guard).
- ☐ First gameplay port (ahead of plan, into Phase 2): 3 weapons (blaster/vortex/machinegun), health/armor/ammo
  items + resources, a vampire mutator on the hook bus; Net serialization + prediction structures.
- ☐ Remaining: install Godot + build the host; CI; golden-trace capture; resolve OPEN Q1/Q2.

## DoD

Solution builds; CI green; the three spikes run; the golden-trace harness exists; architecture decisions
ratified; OPEN Q1/Q2/Q3 resolved.
