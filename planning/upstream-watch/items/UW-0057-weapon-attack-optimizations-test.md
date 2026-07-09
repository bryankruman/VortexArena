# UW-0057 — 

- **Source:** `data:terencehill/weapon_attack_optimizations_test@b1f4589a25a9`
- **Kind:** qc-gameplay
- **Base symbols touched:** `qcsrc/common/weapons/weapon/electro.qc:W_Electro_ExplodeComboThink`, `qcsrc/common/weapons/weapon/electro.qc:W_Electro_Orb_ExplodeOverTime`, `qcsrc/common/weapons/weapon/electro.qh:.dmg_last`, `qcsrc/common/weapons/weapon/hook.qc:W_Hook_ExplodeThink`, `qcsrc/common/weapons/weapon/hook.qc:W_Hook_Explode2`, `qcsrc/common/weapons/weapon/hook.qh:dmg-related fields`, `qcsrc/lib/warpzone/common.qc:WarpZone_FindRadius`, `qcsrc/server/client.qc:PlayerThink`, `qcsrc/server/bot/default/navigation.qh:.dmg`
- **Port-worthiness:** low  ·  **Effort:** M
- **Decision:** defer

## What it does / how it works
Weapon attack optimizations: (1) Electro combo explode-over-time refactored from per-tick to 0.05s discrete damage ticks with fractional damage accumulation (ELECTRO_COMBO_OVERTIME_DELAY constant); (2) Hook secondary (grappling hook secondary fire) refactored similarly with 0.05s ticks (HOOK_SECONDARY_DELAY); removes intermediate `.dmg`/`.dmg_edge`/`.dmg_radius`/`.dmg_force` entity fields in favor of direct cvar reads, with `.dmg_last` tracking the fractional remainder; (3) Server-frame weapon firing restriction: uncommented `if(frametime)` gate in PlayerThink loop (was commented "allow firing on move frames"), so weapons only fire once per physics frame instead of per input frame — fixes attack spam on high tickrate; (4) WarpZone_FindRadius fast-path diagnostic test: commented-out fast-path code enabled with debug logging to compare native findradius() results vs WarpZone_FindRadius_Recurse() to catch divergences. Base symbols: qcsrc/common/weapons/weapon/electro.qc (W_Electro_ExplodeComboThink, W_Electro_Orb_ExplodeOverTime); qcsrc/common/weapons/weapon/hook.qc (W_Hook_ExplodeThink, W_Hook_Explode2); qcsrc/lib/warpzone/common.qc (WarpZone_FindRadius); qcsrc/server/client.qc (PlayerThink); qcsrc/server/bot/default/navigation.qh (entity field for bot danger detection).

## Portability
qc-gameplay, server-admin targeting (high-tickrate spam reduction) — maps poorly to Vortex's fixed 66Hz sim and Godot-replaced engine

## Completeness (upstream)
Incomplete: WarpZone test has debug prints (unfinished). Weapon refactors couple to it. No MR, 3mo inactive.

## Quality
Electro/Hook fractional refactor is sound; field cleanup good. But: WarpZone test unfinished, server-frame firing reverses old behavior without rationale, no perf proof.

## Roadmap / design alignment
Xonotic admin pain (high-tickrate), not Vortex goal. WarpZone is engine-replaced. Electro combo overtime is intentionally-absent feature. Server-frame semantics need determinism analysis.

## Recommendation
Defer. Incomplete + stale (3mo) + debug code + no MR. Targets xonotic server spam, not Vortex. Await upstream MR review, WarpZone test split/finalization, and master merge. If stable, revisit in next parity wave to evaluate Electro combo overtime port and server-frame firing semantics vs deterministic fixed-timestep.
