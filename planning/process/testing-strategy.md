# Process — Testing & QA Strategy

Testing is how we make the fidelity contract real and how we keep a 200k-LOC port honest. Layered from fast/cheap
to slow/holistic.

## 1. Unit tests (`XonoticGodot.Tests`) — fast, run on every commit

- **Gameplay logic** is unit-testable because `XonoticGodot.Common` has no Godot dependency
  ([ADR-0008](../decisions/ADR-0008-solution-structure.md)): construct entities, run think/touch, assert state.
- **Framework**: registry enrollment/order/hash, hook dispatch order (First/Last/Any), net serializer
  round-trips (`Serialize`→`Deserialize` identity) with quantization tolerance.
- **Facade services**: string ops (`sprintf`/color codes/UTF-8), math/vectors, cvar store, VFS resolution order.

## 2. Golden-trace tests — the collision fidelity guard (RISK R3)

- Capture `(start, mins, maxs, end, filtermask) → trace_t` tuples from **Darkplaces** on real maps (instrumented
  build or demo replay).
- Assert the C# collision/trace service matches within tolerance (fraction, endpos, plane normal, contents,
  surfaceflags, texture name).
- Run on a corpus of maps spanning brush, patch, and rotated-bmodel cases.
- **Any change under the fidelity contract must show green golden traces.**

## 3. Movement-parity tests — the "feels like Xonotic" guard

- Feed identical **input logs** to the C# movement and to Darkplaces (recorded demo / instrumented build);
  compare position/velocity trajectories over time.
- Assert divergence stays **within the prediction-error-compensation envelope**
  ([ADR-0010](../decisions/ADR-0010-determinism-and-numerics.md)) — i.e. close enough that smoothing hides it.
- Cover the signature cases: strafe-jumping/bunnyhop, ramp slides, stair-stepping, water, jumppads, ground
  friction.

## 4. Determinism tests

- Same input log on two builds/threads (and, when cross-play matters, two architectures) → divergence within
  envelope. Guards RISK R13.

## 5. Network tests

- Simulated-latency/loss harness: assert prediction reconciles cleanly, interpolation is smooth, no rubber-banding
  beyond budget, lag-comp hit registration is correct.
- Snapshot bandwidth budget assertions for the hot path (player/projectile state).
- Build-parity gate: mismatched content hashes are rejected at connect.

## 6. Asset import tests

- Each importer: load a corpus and assert structural invariants (mesh surface counts, bone/tag presence,
  animation ranges from `.framegroups`, material/`surfaceparm` flags). Visual diff a few hero assets manually.
- VFS: `override/` precedence and extension-search resolve to the same file Darkplaces would pick.

## 7. Integration / scene tests (Godot)

- Headless Godot tests that load a map + spawn a player + step the sim N ticks and assert no exceptions, stable
  entity counts, expected spawn entities from the BSP entity lump.

## 8. Manual parity QA (Phase 4–5)

- A/B against Darkplaces per gametype/weapon/mutator: damage numbers, timings, pickup behavior, HUD values.
- Performance: frame-time and GC-pause budgets on target hardware (RISK R5).

## CI gating

- PRs run layers 1–6 (the fast/deterministic ones). Layers 7–8 run on nightly / pre-milestone.
- Coverage targets matter most on `XonoticGodot.Common.Physics`, the facade trace/collision, and the net serializers.
- The **no-Godot-reference rule on `Common`** is a build-time check.
