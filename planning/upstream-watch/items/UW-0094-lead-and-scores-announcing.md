# UW-0094 — Draft: Implemented lead and scores announcing

- **Source:** `data:z411/leadannce@8b76496ffab6`
- **Kind:** qc-gameplay
- **Base symbols touched:** `WinningConditionHelper`, `WinningConditionHelper_winnerteam_last`, `WinningConditionHelper_winner_last`, `WinningConditionHelper_equality_one`, `WinningConditionHelper_equality_two`, `Score_NewLeader`, `AnnounceNewLeader`, `AnnounceScores`, `Scores_AnnounceLeads`, `ANNCE_LEAD_GAINED`, `ANNCE_LEAD_LOST`, `ANNCE_LEAD_TIED`
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Implements lead-change and match-score announcements across multiple gamemodes. The branch adds: (1) Lead-tracking infrastructure via Score_NewLeader() + WinningConditionHelper state variables (_winner_last, _winnerteam_last, _equality_one/_two) to detect leader changes. (2) New announcer notifications: LEAD_GAINED/LOST/TIED (for FFA), TEAM_LEADS_TEAM/ENEMY/TIED (for team play), TEAM_SCORES_TEAM/ENEMY (post-capture scores), TEAM_WINS (match victory), ROUND_TEAM_WIN/ROUND_OVER/ROUND_TIED/ALONE (CA-specific). (3) AnnounceNewLeader() and AnnounceScores(int tm) functions in world.qc that broadcast these notifications at lead changes or on team-score events. (4) Integration hooks in deathmatch.qc (dm_Scores_AnnounceLeads), duel.qc, tdm.qc enabling the lead announcer, and CTF caps triggering score announcements. (5) 21 binary announcer audio files (leads_red/blue/yellow/pink/enemy/team, wins_*, round_win_*, scores_*, round_over/tied, alone). (6) Three new config cvars (notification_ANNCE_LEAD_GAINED/LOST/TIED) at gentleness levels. Touches Base files: qcsrc/server/scores.qc/qh, qcsrc/server/world.qc, qcsrc/server/mutators/events.qh, qcsrc/common/gamemodes/gamemode/{deathmatch,duel,tdm,ctf}/sv_*.qc, qcsrc/common/notifications/all.inc, notifications.cfg, sound/announcer/default/*.ogg.

## Portability
qc-gameplay — the lead/score tracking logic lives in server/scores.qc and world.qc (authority layer), and the announcer notifications use the existing NotificationSystem which is fully ported in XonoticGodot. Notification definitions (all.inc) map to NotificationsList.cs registration. The audio assets are ported as-is (OGG files). The mode-specific hooks (Scores_AnnounceLeads mutator callhook) are the standard hook pattern. High portability: no engine dependencies, no netcode redesign required — straightforward QC→C# port of the lead-change detect + notification-send flow.

## Completeness (upstream)
DRAFT/WIP branch (OPEN MR). The commits show incremental development: early commits add infrastructure (lead tracking, notification definitions, audio files), later commits refine logic (merge branches, fix test compilation, handle queuetime). The branch is feature-complete for its stated scope (lead + score + round announcements) but marked Draft, indicating it awaits final polish or review gates. No test files visible in the diff; announcer logic relies on integration testing.

## Quality
Good foundational quality with minor rough edges. The infrastructure (Score_NewLeader, AnnounceNewLeader) is cleanly factored. Lead-state tracking via _winner_last/_winnerteam_last uses one-shot gating (correct pattern). The teamplay branch properly branches on equality vs top-two scores. Code style matches surrounding Base QC. Minor issues: (1) AnnounceScores(int tm) is a setter only — actual broadcast happens later in WinningCondition_Scores when team_scores!=0, indirection is sound but not self-documenting. (2) Warmup suppression (time - game_starttime < 1) lacks a named constant or cvar. (3) WinningConditionHelper state bloat (_equality_one/_two alongside _winner/_second) is functional but not elegant. No security issues; asset file count reasonable (~21 OGG files).

## Roadmap / design alignment
Serves Vortex Arena — lead/score announcements improve gameplay feel and are core to team FPS audio feedback. Aligns with port's NotificationSystem (already ported, live). No conflict with intended_divergence (scoring/announcer are actively ported units; this is an *extension*, not a redesign). Grep of planning/ shows no blocking dependencies or roadmap conflicts.

## Recommendation
RECOMMENDED FOR PORTING once the upstream MR is merged or de-drafted. Implementation is sound; testing surface is narrow (lead-tracking + broadcast). Integration effort ~150 LOC C# (state tracking + mode hooks) + file copies. No netcode/determinism concerns. Suggest: (1) Monitor upstream MR for merge or blocking gates. (2) On acceptance, update parity/scoring + parity/cl-announcer with new ANNCE rows. (3) Verify no NotificationSystem changes needed. (4) Plan integration test coverage for lead-change edges (tie → lead → tie scenarios).
