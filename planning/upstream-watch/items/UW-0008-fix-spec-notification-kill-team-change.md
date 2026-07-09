# UW-0008 — Merge "Fix missing spec notification in CTS, Race, LMS; kill players who switch to spectators"

- **Source:** `data@bad2e6d34ef3`
- **Kind:** qc-gameplay
- **Base symbols touched:** `FRAGS_PLAYER`, `FRAGS_PLAYER_OUT_OF_GAME`, `FRAGS_SPECTATOR`, `FRAGS_PLAYER_NONSOLID`, `Damage (deathtype handling)`, `SetPlayerTeam`, `KillPlayerForTeamChange (removed)`, `ClientKill_Now (removed)`, `MakePlayerObserver hooks (CTS/Race/LMS/Freezetag)`, `GiveFragsForKill`, `ClientKill_Now mutator hook (removed)`, `Player_ChangeTeamKill mutator hook (removed)`, `INFINITY constant`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Consolidates spectator-transition and player-elimination logic: unifies team-change + kill-command kill paths (removes KillPlayerForTeamChange wrapper), introduces INFINITY damage sentinel, moves spec-notification sends into central GiveFragsForKill hook (fixes missing centerprints in CTS/Race/LMS), redefines frag-status constants (FRAGS_PLAYER 0→-1, adds FRAGS_PLAYER_OUT_OF_GAME -669), removes dead ClientKill_Now/Player_ChangeTeamKill mutator hooks, adds Freezetag double-kill safety gate. Touches qcsrc/{common/constants.qh, server/{damage.qc,teamplay.qc,clientkill.qc}, common/gametypes/{lms,cts,race,freezetag}/sv_*.qc, client/view.qc}.

## Portability
Fully portable to Vortex Arena's C#/Godot architecture. Affects core game-state constants and damage/gametype-hook flow — no DP-engine specifics, only gameplay logic faithfully mirrored in port's MutatorHooks (OnMakePlayerObserver, OnGiveFragsForKill, OnDamageCalculate) and GameType subsystem. Port already uses class-level game state (LmsState, CtsState, RaceState) rather than frags sentinels, so constant redef is compatible.

## Completeness (upstream)
Production-ready, merged to master via MR !1613 (bones_was_here, 2026-07-06). Five-commit feature branch: all changes are consolidations + bug fixes (no new features). Passes Xonotic CI merge gate. No explicit unit tests in the commit, but prior branch commits address known regressions (missing notifications, double-kills on team change, EventChase flicker), implying test coverage via standard suite.

## Quality
High. Coherent refactoring consolidating three fragmented kill paths (damage/teamplay/clientkill) into one (Damage function in damage.qc), reducing duplication. Removes dead code (KillPlayerForTeamChange wrapper, obsolete hooks). Spec-notification fix is elegant — moved to central GiteFragsForKill locus so it fires exactly once per elimination, matching Base behavior and closing known registry gaps (lms.rules.no_throw_and_spec_lockout: liveness partial→live). Freezetag safety gate (killindicator_teamchange==-2) prevents double-death race. INFINITY constant more self-documenting than hardcoded 100000. Minor risk: FRAGS_PLAYER 0→-1 redef subtle breaking change (any code checking !player.frags will miss init); port likely safe (registry shows state tracked in game-type classes, not frags).

## Roadmap / design alignment
High. Directly fixes spectator-notification gaps flagged in Vortex Arena parity registry (LMS: lms.rules.no_throw_and_spec_lockout partial→faithful; CTS/Race: similar notification gaps). Aligns with port's mutator-hook architecture (OnMakePlayerObserver, OnGiveFragsForKill already wired). No intended-divergence conflicts; port's use of LmsState/CtsState rather than frags sentinels is orthogonal and compatible. Upstream refactoring does not force port design changes, just ensures central damage/kill path is unified upstream.

## Recommendation
Recommended for port after three audits: (1) verify no Vortex Arena code relies on `!player.frags` to detect initialization (registry suggests safe; LMS/CTS/Race use class-level state); (2) test INFINITY damage sentinel in damage pipeline (team-change kills, kill-command, ensure floating-point infinity is handled gracefully); (3) grep port for subscribers to removed hooks ClientKill_Now and Player_ChangeTeamKill (likely none, per registry). Once cleared, porting is mechanical: apply constant changes, consolidate gametype MakePlayerObserver hooks to remove manual frags assignments, unify team-change kill path, wire INFINITY sentinel. Should close multiple parity registry liveness gaps (spec notifications, teamplay unification).
