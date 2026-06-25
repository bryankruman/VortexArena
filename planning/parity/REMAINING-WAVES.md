# Remaining parity waves (after Waves 1–2 + fix-up)

_Generated from the post-implementation registry. ~1,370 open gap-dimensions (~1,080 by the stricter
`PARITY-GAPS.md` "listed-gap" count). Ordered by recommended execution: ROI + dependency._

| Wave | Theme | ~Gaps | High-impact | Notes |
|---|---|---:|---:|---|
| **5** | **Wiring / dead seams** | **~120** | ~63 | Coded-but-not-live features (Assault 2nd-round caller, mutator ItemTouch hooks, weapon FX callers, dead HUD feeders). **Do first** — cheap fixes, big behavioral payoff; mirrors the successful second-pass. |
| **6** | **Gameplay logic & balance** | **~755** | ~218 | THE bulk. Split by category (below). The core gameplay-correctness lever. |
| **7** | **Niche entities** | **~210** | ~0 | turrets ~91 · vehicles ~65 · monsters ~56. Self-contained, lower priority (mostly Onslaught/Assault/Invasion-only). |
| **8** | **Presentation: HUD / CSQC / models / overlays** | **~150** | ~0 | Per-gametype mod-icon render, race-timer feed, frozen/damage overlays, viewmodels, world models. Depends on the server state produced by Waves 5–6. |
| **9** | **Map objects & world physics** | **~80** | ~0 | Remaining func_/trigger_/target_ behaviors, movetypes, the centerprint/reset seams. |
| **10** | **Bot AI & navigation** | **~30** | ~0 | wr_aim alt-fire/detonate/combos, havocbot_dodge, weapon-priority lists, danger-routing producer. |
| **11** | **Audio & announcer cues** | **~25** | ~0 | Remaining un-fired sound cues + announcer edges. |

## Wave 6 sub-batches (the ~755 bulk, split by category for parallel execution)

| Sub | Category | ~Gaps | Examples |
|---|---|---:|---|
| 6a | gametypes | ~190 | Assault 2nd-round machinery, Onslaught link/spawn, Nexball ballstealer, CTS/Race rules, KeyHunt push/destroy |
| 6b | mutators | ~180 | status-effect networking, superspec ItemTouch, instagib/overkill/nades completion, buff perks |
| 6c | server systems | ~135 | mapvoting, teamplay autobalance, commands/votes, intermission, spawnpoints |
| 6d | weapons | ~106 | per-weapon fire-mode/reload/charge details, secondary modes, balance constants |
| 6e | client-logic + physics | ~100 | client-side gameplay calcs (HUD math, prediction), movement edge cases |

## Execution notes
- **Wave 5 first** is the highest ROI: each is a small wiring fix that flips a `dead`/`stub` feature to `live`,
  exactly like the second-pass fix-up that closed 110 gaps with surgical edits.
- Waves 6a–6e are **one-agent-per-file** parallel batches (same harness as Wave 2); run via the batched
  self-retry workflow to ride the throttle, with a `dotnet build` + `dotnet test` gate after each.
- Wave 8 (presentation) should follow 5–6 because most of it **consumes** server state those waves produce
  (a HUD feed is dead until the server sends the value).
- After each wave: `Workflow{name:"parity-diff", args:{scope:"<units>", mode:"update"}}` to re-verify what
  actually closed, then `python tools/parity-assemble.py` to refresh the gap count.
- Counts are approximate and will shift as gaps close and the adversarial verify reclassifies rows.
