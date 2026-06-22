# Parity drift report — 2026-06-22 (selftest)

**Headline:** 0 regressions, 0 new gaps, 0 fixes, 0 reclassified, 2 registry coverage gaps (across 1 unit in scope: `weapon-rifle`). Mode: diff. Registry not rewritten.

Scope: `weapon-rifle`. The unit was flagged `changed` only because of newly-discovered unmapped Base features (coverage gaps); no parity dimension regressed, and no graded change (regression / new-gap / fix / reclassified) was detected.

## Regressions

None. No graded parity dimension got worse.

## New gaps

None.

## Registry coverage gaps

Base features with no corresponding feature row in the registry. Both are faithfully ported and LIVE — these are documentation/coverage gaps in the registry, not parity defects.

| unit | Base feature | status | note |
| --- | --- | --- | --- |
| weapon-rifle | `rifle.qc:wr_suicidemessage` (`METHOD(Rifle, wr_suicidemessage)` -> `WEAPON_THINKING_WITH_PORTALS`) | faithful + LIVE, unmapped | Ported in `DeathMessages.SelectSuicideMessage` (DeathMessages.cs:159): returns `"WEAPON_THINKING_WITH_PORTALS"` for case `"rifle"`, matching the Base hitscan-family easter-egg suicide line. Coverage gap only. |
| weapon-rifle | `rifle.qc:wr_init` (`METHOD(Rifle, wr_init)` — CSQC `precache_pic` of the reticle when `cl_reticle && cl_reticle_weapon`) | faithful + LIVE, unmapped | Trivial client-side reticle precache, touched only obliquely by the `rifle.zoom_eye` notes (ReticleOverlay). No dedicated row. Minor coverage gap. |

## Fixes

None.

## Reclassified

None.
