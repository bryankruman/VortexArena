# UW-0047 — 

- **Source:** `data:k9er/seeker@f66037d92985`
- **Kind:** qc-gameplay
- **Base symbols touched:** `qcsrc/common/weapons/weapon/seeker.qc`, `qcsrc/common/weapons/weapon/devastator.qc`, `qcsrc/server/weapons/common.qc`, `bal-wep-xonotic.cfg`, `qcsrc/common/effects/effectinfo.inc`, `qcsrc/client/csqcmodel_hooks.qc`, `effectinfo.txt`, `qcsrc/common/weapons/all.qc`, `qcsrc/common/weapons/all.qh`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Upstream merges 22 commits adding three major feature groups to the Seeker weapon: (1) Tag guidability — secondary fire (tag dart) can now be laser-guided like Devastator rockets via new W_GuideProjectile helper extracted into qcsrc/server/weapons/common.qc (implements cosine-steering solver for smooth turnrate-limited trajectory toward crosshair); (2) Tag damage — tags deal impact damage on touch (new g_balance_seeker_tag_damage cvar, default 0, + tag_guiderate/guidedelay/guidegoal/guideratedelay balance parameters); (3) Effects overhaul — 18 commits refining particle behavior across Seeker, buffs, nades, and generic effects (new Seeker explosion/trail FX, Buff icon FX for Magnet/Vengeance/Swapper/Vampire, refined alpha-blending for player effects, new tag-strike spark at tag_touch). Base symbols: W_Seeker_Tag_Think (new, guidance state), W_Seeker_Tag_Delete (new, lifecycle), W_GuideProjectile (extracted from Devastator, shared), W_SteerProjectileTo (shared solver), W_Seeker_Fire_Tag (extended with guiderate branching), bal-wep-xonotic.cfg (6 new tag_* params), effectinfo.txt (extensive particle additions/tweaks).

## Portability
qc-gameplay (tag guidance + damage mechanics in qcsrc/common/weapons/) + data-cfg (balance cvars). Effects (data-cfg layer, effectinfo.txt) require ADAPTATION due to Vortex Arena's Godot particle pipeline (DP effectinfo ≠ Godot ParticleSystem3D); gameplay is portable via QuakeC-to-C# translation but necessitates solver implementation.

## Completeness (upstream)
Merged to origin/master; no drafts or open MRs. Appears complete and battle-tested. No formal test coverage in QC, but feature is gated by new cvars (tag_guiderate defaults 0, disabling guidance unless configured), so backward-compatible. Effects are data-driven and intricate (particle lifetimes, fade, texture selects) but finalized.

## Quality
Implementation is clean. Refactor extracts W_GuideProjectile + W_SteerProjectileTo into shared qcsrc/server/weapons/common.qc, eliminating 97 lines of duplicate steering code from Devastator — good DRY principle. Solver (cosine steering via solve_quadratic) is mathematically sound (commented with derivation). Tag guidance respects existing guidance-disabled config (tag_guiderate=0 fallback to simple unguided tag). Code style matches Base conventions. One minor code-smell: W_Seeker_Tag_Think uses fabs(tag_guiderate)<=30 to detect "weak guidance" for trail networking (cosmetic optimization), which is a bit magic-number-ish but explained in inline comment.

## Roadmap / design alignment
High alignment with Vortex Arena gameplay goals. Seeker is already ported (weapon-seeker.yaml lists 11 features, all faithful as of 2026-06-25); tag guidance is a new feature-set that extends existing port without breaking it. Tag damage (default 0) is a balancing toggle, not a breaking change. Effects updates serve QoL + visual feedback (buff icons, refined explosions, tag-strike spark). No conflicts with intended_divergence (none noted in seeker.yaml). Upstream churn score: low (not a cosmetic-only or CI change; adds real gameplay depth).

## Recommendation
Accept (propose: port). The tag guidance feature is a natural extension of the Seeker's identity and reuses the shared W_GuideProjectile framework from Devastator, lowering porting risk. Recommend prioritizing tag guidance + damage (core gameplay) over effects bulk-port; effects can be iterated in a follow-up pass once guidance is live. Verify that tag_guiderate=0 default preserves stock balance for users who do not opt in. Tag guidance should be gated behind a gameplay feature flag during development (e.g., ExperimentalSeekerGuide) to allow iterative balance tuning independent of the main 0.0.1 release cycle.
