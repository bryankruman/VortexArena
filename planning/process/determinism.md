# Determinism & cross-architecture validation

This is the operational companion to [ADR-0010](../decisions/ADR-0010-determinism-and-numerics.md). It defines
what the deterministic simulation guarantees, how that is validated, and what to do when a determinism test
fails. The guards live in `tests/XonoticGodot.Tests/DeterminismTests.cs` and the reusable checksum in
`src/XonoticGodot.Common/Framework/DeterminismHash.cs`.

## What "deterministic" means here

Client-side prediction re-runs the movement simulation and reconciles against authoritative server snapshots
(Xonotic's `CSQCPlayer_SetPredictionError` + `cl_movement_errorcompensation`). So the requirement is **not**
lockstep bit-determinism — it is:

1. **Same-architecture, same-build reproducibility — exact.** The same inputs must produce bit-identical
   results every run. This is what makes prediction match the server *on the same machine class*, makes replays
   stable, and makes the golden-trace tests meaningful.
2. **Cross-architecture (x64 ↔ ARM) — low divergence.** The two need only agree closely enough that the
   prediction-error smoothing hides the difference.

## The numeric contract

| Operation class | Cross-arch behaviour | Used where |
|---|---|---|
| `+ - * /`, `sqrt`, compare, int↔float | **bit-exact** (IEEE-754 correctly rounded on every arch) | the bulk of the physics / accel math |
| integer math (the PRNG) | **bit-exact everywhere** | `Prandom` (PCG) — seeded by the server, replayed by the client |
| `sin cos tan atan2 pow log exp` | **may differ in the last ULP** between platform libms / runtimes | `QMath.AngleVectors` / `VecToAngles`, the air-accel `pow`, the friction `log` |

Discipline (enforced by `Simulation_Source_Has_No_NonDeterministic_Apis`): the simulation source
(`XonoticGodot.Common/Physics`, `XonoticGodot.Common/Math`, `XonoticGodot.Engine/Simulation`) must not read wall-clock time
(`DateTime.Now`, `Stopwatch`, `Environment.TickCount`), use `System.Random`, allocate `Guid.NewGuid`, or call
`*.FusedMultiplyAdd` (FMA contraction changes rounding). Iterate ordered collections only. Single precision
throughout (the port matches a single-precision transcription of the QC).

## The guards

`DeterminismTests` (all run in-suite, serial — the whole suite disables xUnit parallelism because the sim is
ambient on `Api.Services`):

- **`Movement_Trace_Is_Reproducible_Run_To_Run`** — runs the canonical movement scenario twice, asserts the
  `DeterminismHash` (FNV-1a over the exact origin/velocity bits each tick) is identical. The hard same-arch
  guarantee; fails the instant anything non-deterministic touches the movement path.
- **`Movement_Trace_Checksum_Matches_X64_Reference`** / **`QuakeMath_Canonical_Results_Are_Pinned`** — pin the
  trace + a makevectors/vectoangles sweep to their **x64 / .NET reference** checksums. These cover transcendental
  results, so a mismatch means *either* an intended numeric change (re-pin) *or* a platform/arch difference (see
  "When a pin fails").
- **`Prandom_Seeded_Sequence_Is_Pinned`** — pins the seeded PRNG stream. The PRNG is pure integer + a
  power-of-two scale, so these bits are exact on **every** architecture and runtime; this pin should never need
  a platform caveat.
- **`UlpPerturbation_Stays_Within_PredictionEnvelope`** — perturbs the initial velocity by a few ULP (a proxy
  for a cross-arch transcendental difference) and measures the trajectory drift over a client prediction
  window, on ground (friction damps it) and airborne (no friction — the worst case). Both must stay well under
  a 2 qu envelope; the smoothing/teleport threshold is far larger. This is the runnable evidence for ADR-0010's
  "smoothing absorbs the residual" claim — current drift is ~4e-6 qu (ground) / ~4e-5 qu (air).

## Validating x64 ↔ ARM

The project develops on x64; the pins above are the x64/.NET reference. To validate ARM cross-play:

1. Run `DeterminismTests` on the ARM target.
2. **`Prandom_*` and the integer/`+-*/`-only paths must still pass** — if they don't, it is a real bug, not a
   float-tolerance issue.
3. The transcendental-bearing pins (`Movement_*`, `QuakeMath_*`) **may** mismatch. That is the detector working,
   not a regression. Confirm `UlpPerturbation_Stays_Within_PredictionEnvelope` still passes on ARM and, ideally,
   diff an x64 vs ARM trace directly (hash each tick, find the first divergent tick, confirm the per-tick delta
   is ULP-scale and stays within the envelope). If it does, cross-play is sound and the ARM pin can be recorded
   alongside the x64 one.

## When a pin fails

1. Did you intend a numeric change (new physics constant, reordered math)? Re-pin and note why.
2. Otherwise, is it a different arch/OS/runtime? Treat it as a cross-arch detection: run the envelope test and
   the trace diff above. Within the envelope → fine, record the platform's pin. Outside → a real determinism
   bug to fix (look for FMA, an unordered collection, a wall-clock read, double-vs-single drift).
