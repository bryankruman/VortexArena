# UW-0021 — Merge "Mapvoting improvements: networking, antispam method, cleanup"

- **Source:** `data@56297a31eed7`
- **Kind:** qc-gameplay
- **Base symbols touched:** —
- **Port-worthiness:** high  ·  **Effort:** M
- **Decision:** pending

## What it does / how it works
Comprehensive mapvoting refactor: moves mapvoting subsystem from `qcsrc/server/` to `qcsrc/common/` (shared between client/server), extracting networking code to `net.qc`, adding a shared base header `mapvoting.qh`, replacing the 0.5s impulse-polling delay with antispam rate-limiting, improving gametype-title layout, fixing gametype vote stutter, cleaning up code (const → non-const for certain vars, improved formatting), and marking winning map/gametype with bold text. Touches files: `qcsrc/server/mapvoting.{qc,qh}` (deleted), `qcsrc/common/mapvoting/{_mod.inc,_mod.qh,cl_mapvoting.{qc,qh},mapvoting.qh,net.{qc,qh},sv_mapvoting.{qc,qh}}` (added/refactored), plus all client/server includes that reference the old location. Base symbols: `MapVote_*`, `GameTypeVote_*`, `ENT_CLIENT_MAPVOTE`, `TE_CSQC_PICTURE`, antispam rate-limit integration.

## Portability
qc-gameplay. Fully portable to Godot C# (already partially ported in MapVoting.cs / GameWorld.cs). The refactoring is a pure QuakeC reorganization with no engine-specific code; the antispam method reuses existing server-command infrastructure. The networking serialization (ENT_CLIENT_MAPVOTE / TE_CSQC_PICTURE) is n/a to the listen-server Godot port (replaced by in-process object reads).

## Completeness (upstream)
Merged to master (2026-06-18). Fully merged feature branch with 9 commits (move to common, networking extraction, shared header, cleanup, networking improvements, gametype-vote stutter fix, gametype-title layout, impulse-delay→antispam, bold winner). No WIP markers. Appears production-ready; includes commit titles that explain each improvement.

## Quality
High. Code is clean (macro extraction for impulse cases, const→non-const changes justified by reassignment patterns, removed unnecessary braces, improved comment style to doc-comments with ///). The antispam method is a reuse of existing rate-limiting infrastructure (elegant fix). The refactor is structural (moving code from server to common) not logic-changing, reducing risk. No visible hacks or workarounds.

## Roadmap / design alignment
Vortex Arena-serving gameplay improvement. The refactor doesn't change net protocol (EN_CLIENT_MAPVOTE sender/format stays same). Antispam is an UX win (responsiveness + griefing protection) that aligns with Xonotic's general direction. The reorganization (common vs server split) is architectural, not a divergence risk; our MapVoting.cs is already partially derived from Base sv_mapvoting. Gametype-vote UX improvements (stutter fix, bold winner) are quality-of-life wins. No conflicts with our intended_divergence decisions (parity registry notes MapVote_Finished, MapVote_AddVotable, etc. as live/faithful).

## Recommendation
Accept for analysis. This is a real gameplay UX improvement (antispam responsiveness + stutter fix + visual polish) affecting a subsystem we've already ported substantially. Recommend a focused deep-dive doc (UW-0021-mapvoting-improvements.md) to: (1) detail the antispam method and trace how it fits our C# impulse-dispatch (CastVote / Commands.DispatchImpulse), (2) verify the gametype-vote stutter fix applies to our panel render flow, (3) assess effort to backport the bold-winner display, and (4) decide if the antispam cadence should influence our MapVoting.cs rate-limiting tuning. Link to parity registry unit sv-mapvoting.gametype_vote (logic now improved by antispam) and sv-mapvoting.cast.vote (responsiveness direct win).
