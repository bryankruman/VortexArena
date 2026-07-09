# UW-0033 — Merge "T.A.G. Seeker: respect g_projectiles_interact cvar"

- **Source:** `data@729015d1cf54`
- **Kind:** qc-gameplay
- **Base symbols touched:** `W_Seeker_Fire_Missile`, `W_Seeker_Fire_Flac`, `W_Seeker_Fire_Tag`, `W_Seeker_Flac_Touch`, `PROJECTILE_MAKETRIGGER`
- **Port-worthiness:** high  ·  **Effort:** S
- **Decision:** port

## What it does / how it works
Fixes T.A.G. Seeker projectiles to respect g_projectiles_interact cvar by applying the standard PROJECTILE_MAKETRIGGER pattern. Previously the weapon set missile.solid = SOLID_BBOX directly; now it uses PROJECTILE_MAKETRIGGER which sets solid = SOLID_CORPSE (allowing players to pass through projectiles), applies the correct dphitcontentsmask for world/body/corpse collision, and conditionally gates inter-projectile collisions via clipgroup based on the g_projectiles_interact setting. Affects three projectile types: homing missiles (W_Seeker_Fire_Missile), FLAC bolts (W_Seeker_Fire_Flac), and tag darts (W_Seeker_Fire_Tag). Base files touched: qcsrc/common/weapons/weapon/seeker.qc (3 fire functions). Touched: .gitlab-ci.yml (sv_game hash bump only).

## Portability
qc-gameplay — a core QuakeC behavior fix that maps cleanly to the port's Projectiles.MakeTrigger() helper (already defined at Common/Gameplay/Weapons/Projectiles.cs:40-44 as the direct equivalent). The port's MakeTrigger already implements the full Base behavior (SOLID_CORPSE + dphitcontentsmask + deferred clipgroup handling), so the fix is a mechanical application of that existing helper to all three Seeker projectile types (currently using SOLID_BBOX incorrectly).

## Completeness (upstream)
Merged to master 2026-06-11. Clean, focused upstream fix that closes issue #3027. Upstream has tests (sv_game hash bump validates qcsrc compilation). Seeker is already fully ported in Vortex Arena with parity registry coverage, so the port surface is well-defined and already audited.

## Quality
High. The fix is a straightforward application of an established upstream pattern. PROJECTILE_MAKETRIGGER is the canonical helper used by all other weapons (30+ uses in qcsrc/common/). The change is minimal (8 line deletions, 4 line adds), well-reviewed (MR#1609, bones_was_here author), and fixes a real bug — prior to this, Seeker projectiles would incorrectly block player movement due to SOLID_BBOX instead of being transparent (SOLID_CORPSE). The g_projectiles_interact cvar gate was never active for Seeker, unlike all other weapons.

## Roadmap / design alignment
Serves Vortex Arena directly — it is a core gameplay fix, not upstream-only churn. Seeker is an Arena weapon (MUTATORBLOCKED, non-default), but when enabled it should behave faithfully to Base. The port currently has the bug (Solid.BBox at 3 sites in Seeker.cs:209, 359, 434), so porting this fix directly improves the port's accuracy. No existing intended_divergence conflicts (parity registry shows all Seeker features are intended to be faithful).

## Recommendation
Port the fix. Replace all three `missile.Solid = Solid.BBox;` assignments in Seeker.cs (FireMissile line ~209, FireFlac line ~359, FireTag line ~434) with `Projectiles.MakeTrigger(missile);` calls placed AFTER the MutatorHooks.EditProjectile block (matching the Base ordering). The Flac secondary also needs PROJECTILE_TOUCH(this, toucher) in ExplodeFlac's touch handler to ensure warpzone and interaction checks run before detonation (Base seeker.qc:242-244). This brings Seeker in line with all other weapons' projectile setup, fixes the player-pass-through bug, and activates the g_projectiles_interact cvar control that was dormant due to the SOLID_BBOX misuse.
