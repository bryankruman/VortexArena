# UW-0044 — Fix some warpzone interactions

- **Source:** `data:k9er/warpzone-interactions@a1090558abb1`
- **Kind:** qc-gameplay
- **Base symbols touched:** `WarpZone_TraceBox_ThroughZone`, `WarpZone_SearchInRadius`, `WarpZone_TraceLine_ThroughZone`, `WarpZone_RefSys_Add`, `WarpZone_RefSys_AddInverse`, `WarpZone_UnTransformOrigin`, `FL_STATICOWNER`, `te_csqc_trace_callback`, `WarpZone_FindRadius_cond_callback_t`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
42-commit refactoring of warpzone (teleporter/portal) subsystem infrastructure. Core changes: (1) new WarpZone_SearchInRadius(org, radius, nomonsters, callback) replaces WarpZone_FindRadius, filtering entities by condition immediately for O(n) speedup; (2) callback support added to WarpZone_TraceBox/TraceLine tracing; (3) repeated code patterns converted to macros (WarpZone_RefSys_Add/AddInverse); (4) gameplay fixes: CTF flag passing, Nexball safe-pass tracking, projectile seeking, turret/monster attacks, movement mutators now correctly apply warpzone transforms using WarpZone_UnTransformOrigin; (5) new FL_STATICOWNER flag prevents disowning static projectiles after teleport; (6) vector operation optimizations (vdist) and code style normalization. Touched files: qcsrc/lib/warpzone/{common.qc,common.qh}, qcsrc/common/gametypes/gametype/{ctf,nexball}/*, qcsrc/common/weapons/{fireball,crylink,seeker,devastator,arc,hook,vaporizer,shotgun,electro}.qc, qcsrc/common/mutators/mutator/{nades,dodging,walljump,sandbox,buffs,overkill}/*, qcsrc/common/monsters/monster/{golem,mage,spider,wyvern}.qc, qcsrc/common/turrets/*.qc, qcsrc/server/{weapons/tracing.qc,antilag.qc,client.qc,items/items.qc,cheats.qc,portals.qc}. Base symbols: WarpZone_TraceBox_ThroughZone, WarpZone_SearchInRadius, WarpZone_TraceLine_ThroughZone, WarpZone_RefSys_Add, WarpZone_RefSys_AddInverse, WarpZone_UnTransformOrigin, FL_STATICOWNER.

## Portability
qc-gameplay — entirely QuakeC in qcsrc/. Warpzone mechanics already ported to Vortex Arena; this improves correctness and performance of that existing system via refactored APIs (WarpZone_SearchInRadius callback) and bugfixes. No engine internals or special assets.

## Completeness (upstream)
Merged to master (all 42 commits landed). No WIP/draft markers. CI test baseline updated (hash in .gitlab-ci.yml). Clean, production-ready.

## Quality
High. Systematic refactoring with documented intent. Code style consistent with Base norms. Optimizations have measurable goals (fewer redundant field writes on FindRadius, earlier filtering with callback). New callback mechanism is zero-overhead when unused (NOP). No hacks or workarounds observed.

## Roadmap / design alignment
High direct fit. Vortex Arena already has teleporter/warpzone system ported. This branch fixes subtle bugs in how entities (flags, projectiles, monsters) navigate warpzones and how their transforms compose across portal boundaries. No conflicting intended_divergence noted. Netcode neutral — no protocol changes, behavior deterministic.

## Recommendation
Strong port candidate. Warpzone system is a core Vortex Arena subsystem affecting CTF, Nexball, monster/turret attacks, and any map with teleporters. This branch fixes correctness bugs (e.g., CTF flag passing near portals failing to apply inverse transforms) and optimizes entity search (callback filtering prevents redundant O(n²) work on large FindRadius results). Cost is moderate (42 commits, but pattern is repetitive); risk is low (bugfixes + cleanups, no new engine contracts). Recommend Bryan accept as `port` unless integration testing surfaces unexpected Godot-specific issues with the callback mechanism or RefSys composition order.
