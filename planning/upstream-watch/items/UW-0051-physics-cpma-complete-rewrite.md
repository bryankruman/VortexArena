# UW-0051 — New player movement physics: complete CPMA support, known bugs fixed

- **Source:** `data:bones_was_here/ilikephysics4@582a369000d7`
- **Kind:** qc-gameplay
- **Base symbols touched:** `qcsrc/common/physics/player.qc`, `qcsrc/common/physics/player.qh`, `qcsrc/common/physics/movetypes/movetypes.qc`, `qcsrc/common/physics/movetypes/movetypes.qh`, `qcsrc/common/physics/movetypes/walk.qc`, `qcsrc/common/physics/movetypes/step.qc`, `qcsrc/common/stats.qh`, `physics.cfg`, `physicsCPMA.cfg`, `physicsQ3.cfg`, `qcsrc/ecs/systems/physics.qc`, `qcsrc/common/mapobjects/teleporters.qc`, `qcsrc/common/mapobjects/func/plat.qc`, `qcsrc/common/mapobjects/func/button.qc`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Complete rewrite of player movement physics across 20 commits (1166+645- qcsrc lines). Implements full CPMA support and fixes known bugs: core velocity clipping upgraded to id tech 3, ground detection rewritten (frametime-independent jumping), water physics completely rewritten (separate accel/friction/flags/swimscale), Q3 and CPMA step-up behaviors (3 switchable modes), CPMA double jumps with prediction (step/ramp/headbang/teleport), Q3 skimming with gravity fix, Q3-derived unsticking, sloping ground-plane handling, slick/footstep optimizations. Removes legacy step code and standalone doublejump mutator. Touches physics-player (core accel/friction/jump, 19 tracking features), physics-movetypes (clipping/flymove, 13 features), and all preset configs.

## Portability
qc-gameplay — shared client/server QuakeC. Movement physics split across PlayerPhysics (authority) and prediction (client). Directly portable to C#/Godot movement layer; will touch PhysicsPreset infrastructure, ground/air/water branches, clipping, jumping, crouch, and config system. No DP engine dependencies; pure gameplay math and cvar tuning. All 20 commits are self-contained physics improvements with no external dependencies.

## Completeness (upstream)
Merged to branch master-ready; open MR (OPEN state). 20 commits on bones_was_here/ilikephysics4, all marked with „pm:" prefix convention. No WIP markers in titles. Includes pipeline-update commit (hash bump). All physics.cfg variants updated consistently with new cvars. Appears production-ready, not experimental.

## Quality
High — incremental, focused commits with clear titles and surgical changes. Each commit is a self-contained feature (e.g., „rewrite water physics", „implement CPMA double jumps", „upgrade velocity clipping"). Code review signals: removal of legacy code (simplification +38-136), consolidation of doublejump into core (mutator→physics.qc), and fixes to known issues (unsticking, sloping ground). Physics constants re-verified in parity registry against live port (physics-player/physics-movetypes); no regressions in prior audits. Format matches Base codebase (qcsrc/ structure, cvar naming, cfg file patterns).

## Roadmap / design alignment
High alignment with Vortex Arena roadmap. Physics is a primary differentiator: CPMA mode is planned for advanced players, and the port's physics-player/movetypes registries already track faithful reproduction against baseline. This branch directly extends the tracked spec (new modes: steptype 0/2/3, skimming 0/2, crouchflags/wateraccelerate tuning, double-jump frametime-independence). Adds no conflicting divergence — it's upstream-forward without impedance to the port's existing motion fidelity. Touches zero i18n/build/engine internals.

## Recommendation
Port. This is upstream consolidation of physics work proven non-regressive, extending the port's already-live physics fidelity into higher-skill modes (CPMA, Q3 skimming, double-jumps). The 20 commits are surgical, well-organized, and internally coherent. No porting friction expected beyond routine c#-ification. Propose scoping as part of Wave-N advanced-physics pass once Wave M (current) stabilizes; link parity registry rows (physics-player, physics-movetypes) to track extended baseline. Golden-trace tests for each new mode (CPMA step variants, water, crouch, skimming) should be added to the ported test suite (existing MovementParityTests infrastructure already covers Q1/Warsow/base Xonotic).
