# UW-0089 — Draft: Freeze Tag: players spawn with some lives and get eliminated if they are fragged after they lost all their lives

- **Source:** `data:terencehill/_freezetag_lives@70cd14a8af78`
- **Kind:** qc-gameplay
- **Base symbols touched:** `qcsrc/common/gamemodes/gamemode/freezetag/sv_freezetag.qc:freezetag_Get_Start_Lives`, `qcsrc/common/gamemodes/gamemode/freezetag/sv_freezetag.qc:ft_lowest_lives`, `qcsrc/common/gamemodes/gamemode/freezetag/sv_freezetag.qc:MUTATOR_HOOKFUNCTION(ft, ForbidSpawn)`, `qcsrc/common/gamemodes/gamemode/freezetag/sv_freezetag.qc:MUTATOR_HOOKFUNCTION(ft, MakePlayerObserver)`, `qcsrc/common/gamemodes/gamemode/freezetag/sv_freezetag.qc:MUTATOR_HOOKFUNCTION(ft, PlayerDies)`, `qcsrc/common/gamemodes/gamemode/freezetag/sv_freezetag.qc:MUTATOR_HOOKFUNCTION(ft, PlayerSpawn)`, `qcsrc/common/gamemodes/gamemode/freezetag/sv_freezetag.qc:MUTATOR_HOOKFUNCTION(ft, PutClientInServer)`, `qcsrc/common/gamemodes/gamemode/freezetag/sv_freezetag.qc:MUTATOR_HOOKFUNCTION(ft, SpectateSet)`, `qcsrc/common/gamemodes/gamemode/freezetag/sv_freezetag.qc:MUTATOR_HOOKFUNCTION(ft, SpectateNext)`, `qcsrc/common/gamemodes/gamemode/freezetag/sv_freezetag.qc:MUTATOR_HOOKFUNCTION(ft, SpectatePrev)`, `qcsrc/common/gamemodes/gamemode/freezetag/sv_freezetag.qc:MUTATOR_HOOKFUNCTION(ft, Bot_FixCount)`, `qcsrc/common/gamemodes/gamemode/freezetag/sv_freezetag.qc:FT_SpectateNext`, `gamemodes-server.cfg:g_freezetag_frozen_lives`, `gamemodes-server.cfg:g_freezetag_spectate_enemies`, `notifications.cfg:CENTER_FREEZETAG_LIVES_REMAINING`, `qcsrc/common/notifications/all.inc:MSG_CENTER_NOTIF(FREEZETAG_LIVES_REMAINING)`
- **Port-worthiness:** medium  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Implements Freeze Tag lives system: players spawn with configurable lives (g_freezetag_frozen_lives, default 2) that decrement per freeze; reaching 0 triggers elimination. Late joiners spawn at team's current minimum lives to prevent rejoin exploits. Adds spectator-control cvar (g_freezetag_spectate_enemies: -1=locked, 0=teamonly, 1=free) with new SpectateSet/Next/Prev hooks. Includes CENTER_FREEZETAG_LIVES_REMAINING notification and multiple edge-case fixes (team-switch mid-round, self-kill message gating, blocked spawn when any player eliminated). Base files: qcsrc/common/gamemodes/gamemode/freezetag/sv_freezetag.qc (~400 LOC across multiple hooks), gamemodes-server.cfg, notifications.cfg, qcsrc/common/notifications/all.inc, qcsrc/server/world.qc, ruleset-XPM.cfg.

## Portability
qc-gameplay — lives system is pure logic with no engine/protocol changes. Spectator control uses standard mutator hooks already wired in Vortex Arena. Notification system already mapped. Porting requires refactoring lives-tracking fields + hooks into FreezeTag.cs entity lifecycle + ReviveTick context, and wiring SpectateSet/Next/Prev hooks onto MutatorHooks. Straightforward but non-trivial due to player lifecycle complexity.

## Completeness (upstream)
Upstream state: open MR (draft). Appears finished — 7 commits with bug fixes applied iteratively; last commit (70cd14a8af78) addresses elimination edge cases and message gating. No explicit tests in diff. Implementation appears solid and complete for review.

## Quality
Clean, well-structured QC matching surrounding style. Lives tracking is correctly modeled (start lives vs. current lives, -1 for eliminated). Edge cases handled well: warmup prevention, team-switch unfreeze logic, late-join minimum-lives, spectator enforcement via new hooks, elimination gating via ForbidSpawn. Bug fixes demonstrate thoughtful refinement (self-kill message, team-change respawn, observer transition).

## Roadmap / design alignment
Real gameplay feature adding strategic depth (resource management, tactical revive choices), not upstream churn. No conflict with Vortex Arena design goals in planning/ docs. Not yet tracked in parity registry (new feature upstream). Potential port once gameplay stable, but feature adoption is a design decision (always-on lives vs. server-configurable).

## Recommendation
Solid implementation of a real FT gameplay feature with clean code and good edge-case handling. No engine changes or protocol additions. Moderate complexity due to player lifecycle + new spectator hooks + lives-field tracking. Risk is moderate: lives decrement must respect warmup state, team-switch handling is complex, ForbidSpawn gates all spawns when any player eliminated. Port effort is M, straightforward refactoring into FreezeTag.cs. Decision needed: does Vortex Arena want the lives system in FT? Once decided (port/defer/reject), the work is clear.
