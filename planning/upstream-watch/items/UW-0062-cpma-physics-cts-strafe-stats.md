# UW-0062 — CPMA/Q3 physics rewrite + CTS strafe/race stats (server-ilikephysics4)

- **Source:** `data:morosophos/server-ilikephysics4@ba4fcb77dd6f` (branch, ~251 ahead of master, last updated 2026-02-02; relates to open MR !1579 "New player movement physics: complete CPMA support")
- **Kind:** qc-gameplay
- **Base symbols touched:** `qcsrc/ecs/systems/physics.qc`, `qcsrc/common/physics/movetypes/*.qc`, `qcsrc/server/strafe.qc` (new), `qcsrc/server/strafe.qh` (new), `qcsrc/server/race.qc`/`race.qh`, `qcsrc/common/scores.qh` (SP_CTS_* fields), `qcsrc/common/stats.qh` (Q3COMPAT_JUMPPADS), `physics*.cfg` presets
- **Port-worthiness:** high  ·  **Effort:** L (physics M–L + strafe/race S–M + scoreboard S)
- **Decision:** pending
- **Related ledger items:** UW-0051 (the physics rewrite `bones_was_here/ilikephysics4`), UW-0055 (strafe stats). This branch bundles both.

## What it does / how it works
Two layers merged into one in-flight branch. **(1) Physics rewrite** (~23 `pm:` commits): rewrites
ground detection (frametime-independent jumping), water physics (separate accel/friction/flags),
Q3/CPMA step-up behaviours, CPMA double jumps *with prediction*, Q3 skimming, and upgrades core
velocity clipping to id tech 3; removes the legacy standalone doublejump mutator by folding it into
core. **(2) CTS/speedrun server additions** on top: a new `qcsrc/server/strafe.qc`
(`calculate_strafe_efficiency`, ~188 lines, branches on onground/swimming/air using `PHYS_*`
constants) plus `race.qc` accumulators tracking per-run `startspeed`/`avgspeed`/`topspeed`/
`strafe_efficiency`, surfaced via new scoreboard fields `SP_CTS_STRAFE`/`STARTSPEED`/`AVGSPEED`/`TOPSPEED`.

## Portability
Pure qc-gameplay, no DP-engine dependency. The physics math maps onto the port's C# movement core
(`PlayerPhysics.Move` / `PMAccelerate` and the movement-constants infra); the strafe calculator is
self-contained and portable to a C# `StrafeMeter`-style helper; the race stat accumulators hook the
existing player-physics path mechanically; the `physics*.cfg` presets are a straight data port. No
architectural friction expected on either layer.

## Completeness (upstream)
The physics half (`bones_was_here/ilikephysics4`) is mature and review-ready upstream (self-contained
commits, no WIP markers). The `morosophos` merge (2026-02-02) layers the strafe/race additions, which
also read as complete. Not yet merged to upstream master (still far ahead). Validation pathway exists
(CTS spawn/physics test infra referenced in parity process docs).

## Quality
High. Surgical, well-titled one-feature-per-commit history; healthy consolidation (legacy doublejump
mutator removed, folded into core); fixes known edge cases (unsticking, sloping ground). Strafe
calculator is physics-grounded with proper swimming/onground/air branching. Matches Base style.

## Roadmap / design alignment
Strong on both layers, **but overlaps the port's active movement-parity effort** — read
[[strafe-parity-investigation]] and [[bhop-crippled-jump-downtrace-grant]] before porting: the port
currently chases *faithful Base movement* (GAMEPLAYFIX_Q2AIRACCELERATE, wishmove 360, downtrace
grant), and this branch is a *different, forward* physics model (CPMA/Q3). Decide whether Vortex Arena
wants CPMA as an additional mode vs a baseline change. CTS/racing is a confirmed port target
(`parity/registry/cts.yaml`); strafe efficiency + per-run stats are core to the racing pillar.

## Recommendation
Accept in principle but **Bryan's call on sequencing/scope**. If accepted: port in two phases — Phase 1
the physics rewrite (UW-0051) as a parity-registry extension of `physics-player`/`physics-movetypes`
to CPMA/Q3 modes; Phase 2 the strafe/race stats (UW-0055) atop it. The layers unbundle cleanly (no
interacting deps, just chronological layering). Reconcile with the in-flight `claude/movement-fixes`
work first so the two movement efforts don't collide.
