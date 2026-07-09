# UW-0052 — 

- **Source:** `data:terencehill/weapon_attack_optimizations_TEST@d87946b0741f`
- **Kind:** qc-gameplay
- **Base symbols touched:** `WarpZone_FindRadius`, `W_Electro_ExplodeComboThink`, `W_Hook_ExplodeThink`, `W_Fireball_LaserPlay`, `napalm_damage`, `ELECTRO_COMBO_OVERTIME_DELAY`, `HOOK_SECONDARY_DELAY`, `vlen2`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Performance optimizations for weapon damage calculations: WarpZone_FindRadius fast-path (skip expensive warpzone transforms when no warpzones in radius); squared-distance comparisons in Fireball/Napalm LaserPlay (avoid repeated sqrt); frame-decoupled damage ticks for Electro combo and Hook secondary (0.0625s intervals instead of per-frame); server-frame-only weapon firing gate; entity field cleanup. Base symbols: qcsrc/lib/warpzone/common.qc/.qh, qcsrc/common/weapons/weapon/{electro,hook,fireball}.qc/.qh, qcsrc/common/mutators/mutator/nades/nade/napalm.qc/.qh, qcsrc/server/client.qc, qcsrc/server/bot/default/navigation.qh.

## Portability
Fully portable to C#/Godot weapon system. WarpZone fast-path logic (teleporter search optimization) replicable; squared-distance comparisons trivial; tick-rate decoupling (0.0625s intervals) direct port; field cleanup harmless.

## Completeness (upstream)
WIP branch (not yet merged to master). 10 commits, sound logic. Includes debug test code (d87946b07: WarpZone comparison harness with cvar('aaa') logging, array size 99) that should be stripped before porting. No formal test suite; validation via inline logging. Incremental commits, well-named.

## Quality
Good. Follows Base code style; squared-distance math correct; tick-rate calculations intentional (0.0625s = 1/16, exact multiple of default frametime 0.015625 = 1/64); WarpZone fast-path safe (checks warpzone_warpzones_exist predicate). Includes sync notes for duplicate logic (Fireball/Napalm). No obvious bugs.

## Roadmap / design alignment
Serves both upstream (CPU optimization) and Vortex Arena (performance + determinism for networked C# Godot). No design conflicts; baseline improvements, not upstream-specific churn. Frame-decoupled tick rates align with Vortex Arena's frame-stepping authority model.

## Recommendation
Port, with caveat: drop debug test commit (d87946b07) and verify WarpZone test harness logging is disabled in the final merge. The 9 core optimization commits are sound and improve both performance and determinism for hot-path weapon damage. When porting, mirror tick-rate constants (ELECTRO_COMBO_OVERTIME_DELAY, HOOK_SECONDARY_DELAY) and squared-distance logic in weapon damage codepaths. Add parity registry rows for Electro/Hook tick-rate changes and Fireball/Napalm optimization once ported.
