# ADR-0010 — Determinism approach: low-divergence float + error smoothing

**Status:** Accepted

## Context

Client-side prediction re-runs the movement simulation and reconciles against server snapshots, so client and
server must produce *closely matching* results from identical inputs. Strict cross-machine bit-determinism of
floating point is the classic hard problem (FMA contraction, SIMD reordering, `MathF` fast paths, x64-vs-ARM
differences). Darkplaces runs QC arithmetic at **double precision**.

Crucially, **Xonotic's existing CSQC netcode already tolerates and smooths prediction error**
(`CSQCPlayer_SetPredictionError` + `cl_movement_errorcompensation`, with large deltas treated as
teleports/jumppads and ignored). So the system does *not* require lockstep bit-determinism — only *low
divergence* between two runs of the same code.

## Decision

Target **low-divergence deterministic float**, not strict lockstep:

- Run the simulation on a **fixed 72 Hz timestep** with the same code path on client and server.
- Use `double` for accumulation in the movement/collision math where the QC did (audit the physics paths);
  `float` elsewhere is fine.
- **Discipline:** avoid FMA contraction and aggressive fast-math in the sim hot path; avoid SIMD that reorders
  reductions; pin numeric behavior; use a **deterministic PRNG** seeded from the server (Xonotic already
  broadcasts a seed).
- **Rely on the existing error-compensation/smoothing** as the safety net for residual divergence.
- Add a **determinism test**: run the same input log on two builds/threads and assert divergence stays within the
  error-compensation envelope. **(Implemented — `tests/XonoticGodot.Tests/DeterminismTests.cs` + the reusable
  `DeterminismHash`: same-run bit-reproducibility, pinned x64 trace/PRNG/math checksums as the cross-arch
  detector, a ULP-perturbation→prediction-window-drift envelope check, and a forbidden-API source guard. See
  [planning/process/determinism.md](../process/determinism.md).)**

Revisit only if measured divergence exceeds what smoothing hides (then consider fixed-point for the movement
core).

## Consequences

- Avoids the large cost and feel-impact of fixed-point software math up front.
- Cross-architecture cross-play (x64↔ARM) carries residual risk (R13) — validated by the determinism test; the
  smoothing envelope is the buffer.
- The fixed timestep and deterministic RNG are mandatory and shape the sim core
  ([ADR-0004](ADR-0004-deterministic-simulation.md)).

## Alternatives considered

- **Strict fixed-point lockstep:** deferred — likely over-engineering given the existing smoothing; high cost,
  changes feel.
- **Full bit-exact double IEEE everywhere:** infeasible to guarantee across architectures; unnecessary given
  smoothing.
