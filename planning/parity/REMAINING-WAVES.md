# Remaining parity waves

_Rewritten 2026-06-27. The original Waves 5–11 roadmap was fully executed, then extended by residual-sweep
Waves 12–15. This file now tracks the **final two porting waves (16–17)** that close the long tail, plus a
verification follow-up. Read [EXECUTION-STRATEGY.md](EXECUTION-STRATEGY.md) for the harness + model-tiering._

## Done

Waves **1–2 + fix-up · 5 · 6a–6e · 7 · 8a–8e · 9–11 · 12a–12d · 13 · 14a–14b · 15a–15c · 16 · 17** are
complete and committed, each re-verified with 0 regressions. See `git log --grep parity`.

- **Wave 16 — state producers** (66 units, 244 edits) → `b3a9c06`; build green, 2854 tests / 0 failed.
- **Wave 17 — presentation consumers** (46 units, 134 edits) → `2f0bfdb`; build green, 2854 tests / 0 failed.
- **Consolidated re-verify** (112 units, 387 dimensions closed, 0 regressions) → `44252f3` + this pass; see
  [DRIFT-2026-06-27-waves16-17.md](DRIFT-2026-06-27-waves16-17.md).

## Current state (2026-06-27, post Waves 16/17)

`PARITY-GAPS.md`: **465 features carry an open gap-dimension** (down from 524). The makeup is now overwhelmingly
*fidelity polish* + deferred *feature builds*, not absent core behaviour:

| worst dim | before → now | meaning |
|---|---|---|
| presentation:partial | 123 → 113 | works, not pixel-faithful |
| logic:partial | 116 → 110 | mostly-correct, edge cases unported |
| logic:**missing** | 78 → **54** | genuinely absent behaviour |
| presentation:**missing** | 65 → **43** | absent visual |
| liveness:partial/dead | 68 → 69 | coded but partly/not on the live path |
| values/timing/audio | ~38 | constants / framerates / un-fired cues |

## What remains — proposed Wave 18 (feature builds) + verification

The surgical-edit gaps are largely closed. What's left clusters into two buckets that are **not** one-agent-per-
file surgical work:

1. **Wave 18 — feature builds** (the 357 `unportable` items): cross-cutting subsystems each needing their own
   spec + Net/render plumbing — **waypoint-sprite networking + HUD/radar render** (Assault/CTF/ONS objective
   markers), **wepent crosshair-ring + LAST_PICKUP networking** (remote-client weapon/pickup HUD), bot scripting
   VM (`bot_cmd`), in-game waypoint editor, jetpack point-to-point navigation, ballistic `tracetoss` aim,
   warpzone see-through render (SubViewport), hook rope-line, porto trajectory preview, vehicle/turret world
   models. Best run as a small number of *dedicated* feature tasks, not a fan-out.
2. **Verification** — the 33 [NEEDS-INGAME-CHECK.md](NEEDS-INGAME-CHECK.md) items need a live run (`/verify`).

## The plan — 2 porting waves (dependency-minimal)

The only hard ordering is **state producers → presentation consumers** (a HUD/render feed is dead until the
server produces its value). Foundation folds into Wave 16 (its primitives land first inside the apply
ordering). That makes **two** porting waves the minimum; conflicts are handled inside each wave by the
plan→apply harness, not by splitting waves.

### Wave 16 — State producers · 66 units · 285 gaps
foundation (4) + gameplay (51) + server-admin (9) + bot (2). Everything server-side/networking/logic that
produces gameplay state and the wire values presentation needs.
- Plan-phase tiers: **49 opus · 13 sonnet · 4 haiku**. Apply/gate/verify: **all opus**.
- Headliners: ClanArena INGAME-state machine, Freezetag frag/alone/ice completion, CTS checkpoints, Nexball
  carrier state, Onslaught link/spawn, weapon edge-cases (minelayer, vortex rot-decay, overkill), the 4
  foundation primitives (cl-csqcmodel, net-entity-state, sv-client-lifecycle, sv-world-rules), bot AI roles.

### Wave 17 — Presentation consumers · 46 units · 222 gaps
HUD panels, CSQC render, world models, overlays, viewmodels, vehicle/monster/turret visuals — consumes the
state Wave 16 produces.
- Plan-phase tiers: **42 opus · 4 haiku**. Apply/gate/verify: **all opus**.
- Headliners: Onslaught client presentation, warpzone see-through render, hook rope-line, porto trajectory
  preview, freezetag ice model, vehicle/monster render fidelity, scoreboard item-stats/interactive UI.

### Follow-up — Verification (not a porting wave)
The 30 [NEEDS-INGAME-CHECK.md](NEEDS-INGAME-CHECK.md) items are fidelity/liveness that only a live run
confirms (HUD timing, water/ladder/jetpack physics, bugrigs feel). Cleared by running the app (`/verify`),
not by code agents.

## Execution loop
For each wave: run the `_wave-port.workflow.js` Workflow → confirm `dotnet build`/`dotnet test` green →
`git commit` + `git push` → run `parity-diff` re-verify (folded into the wave's Verify phase) →
`python tools/parity-assemble.py` to refresh the gap count → launch the next wave.
